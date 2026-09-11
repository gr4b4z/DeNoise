using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;

namespace AlertHub.Application.Mapping;

/// <summary>Raw input to a mapping: the body bytes and the stored request headers.</summary>
public sealed record RawInput(ReadOnlyMemory<byte> Body, IReadOnlyDictionary<string, string> Headers)
{
    public JsonNode? ParseBody()
    {
        try
        {
            return JsonNode.Parse(Body.Span, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new MappingException($"body is not valid JSON: {ex.Message}", null, ex);
        }
    }

    public JsonObject HeadersAsJson()
    {
        var obj = new JsonObject();
        foreach (var (k, v) in Headers) obj[k] = v;
        return obj;
    }
}

public sealed record MappingContext(Guid IntegrationId, Guid EventId, DateTimeOffset ReceivedAt, int MappingVersion);

/// <summary>Interpreted result before persistence.</summary>
public sealed record MappingResult(NormalisedEvent Event, IReadOnlyDictionary<string, JsonNode?> Fields, bool OccurredAtFromSource);

/// <summary>
/// Interprets one raw event with one mapping document (07 §1): fields → required check → enrich → identity →
/// delivery key. Deterministic; no I/O; every failure is a <see cref="MappingException"/> with the field name.
/// </summary>
public static class MappingEngine
{
    public static bool AppliesTo(MappingDocument doc, JsonNode? body, JsonObject headers)
    {
        if (doc.AppliesWhen is null) return true;
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        return PredicateEvaluator.Evaluate(doc.AppliesWhen, reference => Resolve(reference, body, headers, fields));
    }

    public static MappingResult Normalise(MappingDocument doc, RawInput input, MappingContext context)
    {
        var body = input.ParseBody();
        var headers = input.HeadersAsJson();
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        foreach (var (name, rule) in doc.Fields)
        {
            var value = EvaluateRule(rule, name, body, headers, fields);
            if (CanonicalFields.ObjectFields.Contains(name) && value is not null && value is not JsonObject)
            {
                throw new MappingException($"'{name}' must map to an object", name);
            }
            fields[name] = value;
        }

        foreach (var required in doc.Required)
        {
            if (ValueCoercion.IsEmpty(fields.GetValueOrDefault(required)))
            {
                throw new MappingException($"required field '{required}' is missing or empty", required);
            }
        }

        foreach (var step in doc.Enrich)
        {
            if (step.WhenEmpty && !ValueCoercion.IsEmpty(fields.GetValueOrDefault(step.Set))) continue;
            var key = ValueCoercion.AsDisplayString(fields.GetValueOrDefault(step.Key));
            if (key.Length == 0 || !doc.LookupTables.TryGetValue(step.LookupTable, out var table) || !table.TryGetValue(key, out var found)) continue;
            fields[step.Set] = JsonValue.Create(found);
        }

        var eventType = ValueCoercion.AsDisplayString(fields.GetValueOrDefault(CanonicalFields.EventType)).Trim().ToLowerInvariant();
        if (!EventTypes.IsKnown(eventType))
        {
            throw new MappingException($"event_type '{eventType}' is not one of {string.Join("|", EventTypes.All)}", CanonicalFields.EventType);
        }

        var occurredAtNode = fields.GetValueOrDefault(CanonicalFields.OccurredAt);
        DateTimeOffset? occurredAt = ValueCoercion.IsEmpty(occurredAtNode) ? null : ValueCoercion.ParseTimestamp(occurredAtNode!, CanonicalFields.OccurredAt);
        var occurredAtFromSource = occurredAt is not null;

        var severityText = ValueCoercion.AsDisplayString(fields.GetValueOrDefault(CanonicalFields.Severity));
        var severity = SeverityExtensions.ParseWire(severityText);

        var dimensions = ValueCoercion.AsStringObject(fields.GetValueOrDefault(CanonicalFields.Dimensions));
        var labels = ValueCoercion.AsStringObject(fields.GetValueOrDefault(CanonicalFields.Labels));

        string? fingerprint = null;
        IReadOnlyList<IdentityComponent>? components = null;
        var carriesIdentity = eventType is not (EventTypes.Heartbeat);
        if (carriesIdentity)
        {
            var inputs = doc.Identity.Select(spec => IdentityInputFor(spec, body, headers, fields)).ToList();
            try
            {
                var fp = Fingerprint.Compute(context.IntegrationId, doc.IdentityVersion, inputs);
                fingerprint = fp.Hex;
                components = fp.Components;
            }
            catch (MissingIdentityComponentException) when (eventType == EventTypes.Informational)
            {
                // informational events may legitimately lack identity; they are then not deduplicated into an episode
            }
            catch (MissingIdentityComponentException ex)
            {
                throw new MappingException(ex.Message, ex.Component, ex);
            }
        }

        var sourceAlertId = Text(fields, CanonicalFields.SourceAlertId);
        var sourceEventId = Text(fields, CanonicalFields.SourceEventId);
        var sourceVersion = Text(fields, CanonicalFields.SourceVersion);
        var deliveryKey = DeliveryKey.Compute(sourceEventId, sourceAlertId, sourceVersion, eventType, occurredAt, occurredAtFromSource, input.Body.Span, doc.IgnorePathsForBodyKey);

        var evt = new NormalisedEvent
        {
            EventId = context.EventId,
            IntegrationId = context.IntegrationId,
            RawReceivedAt = context.ReceivedAt,
            ReceivedAt = context.ReceivedAt,
            MappingVersion = context.MappingVersion,
            EventType = eventType,
            SourceAlertId = sourceAlertId,
            SourceEventId = sourceEventId,
            SourceVersion = sourceVersion,
            OccurredAt = occurredAt,
            Severity = severity,
            SourceSeverity = Text(fields, CanonicalFields.SourceSeverity) ?? (severityText.Length > 0 ? severityText : null),
            ResourceId = Text(fields, CanonicalFields.ResourceId),
            ResourceName = Text(fields, CanonicalFields.ResourceName),
            RuleId = Text(fields, CanonicalFields.RuleId),
            RuleName = Text(fields, CanonicalFields.RuleName),
            Environment = Text(fields, CanonicalFields.Environment),
            Service = Text(fields, CanonicalFields.Service),
            Summary = Text(fields, CanonicalFields.Summary),
            SourceUrl = Text(fields, CanonicalFields.SourceUrl),
            RunbookUrl = Text(fields, CanonicalFields.RunbookUrl),
            Dimensions = dimensions,
            Labels = labels,
            Fingerprint = fingerprint,
            IdentityComponents = components,
            DeliveryKey = deliveryKey.Key,
            IdentityConfidence = deliveryKey.Confidence,
            LifecycleProfileHint = doc.LifecycleProfileHint,
        };
        return new MappingResult(evt, fields, occurredAtFromSource);
    }

    private static string? Text(Dictionary<string, JsonNode?> fields, string name)
    {
        var node = fields.GetValueOrDefault(name);
        if (ValueCoercion.IsEmpty(node)) return null;
        return ValueCoercion.AsDisplayString(node);
    }

    private static IdentityInput IdentityInputFor(IdentitySpec spec, JsonNode? body, JsonObject headers, Dictionary<string, JsonNode?> fields)
    {
        JsonNode? value = spec.Path is not null
            ? JsonPathEvaluator.First(spec.Path, body, headers)
            : fields.GetValueOrDefault(spec.Field!);
        if (ValueCoercion.IsEmpty(value)) return new IdentityInput(spec.Name, null, null, spec.Optional, spec.CaseInsensitive);
        if (value is JsonObject obj)
        {
            var map = ValueCoercion.AsStringObject(obj)?.ToDictionary(kv => kv.Key, kv => Transforms.Apply(kv.Value, spec.Transform), StringComparer.Ordinal);
            return new IdentityInput(spec.Name, null, map, spec.Optional, spec.CaseInsensitive);
        }
        return new IdentityInput(spec.Name, Transforms.Apply(ValueCoercion.AsDisplayString(value), spec.Transform), null, spec.Optional, spec.CaseInsensitive);
    }

    /// <summary>Resolves a predicate/template reference: JSONPath against body/headers, or an already-mapped canonical field.</summary>
    internal static JsonNode? Resolve(string reference, JsonNode? body, JsonObject headers, IReadOnlyDictionary<string, JsonNode?> fields)
    {
        if (reference.StartsWith('$')) return JsonPathEvaluator.First(reference, body, headers);
        if (fields.TryGetValue(reference, out var direct)) return direct;
        var dot = reference.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && fields.TryGetValue(reference[..dot], out var parent) && parent is JsonObject obj)
        {
            return obj[reference[(dot + 1)..]];
        }
        return null;
    }

    private static JsonNode? EvaluateRule(MappingRule rule, string field, JsonNode? body, JsonObject headers, Dictionary<string, JsonNode?> fields)
    {
        if (rule.When is not null && !PredicateEvaluator.Evaluate(rule.When, r => Resolve(r, body, headers, fields)))
        {
            return rule.HasDefault ? rule.Default?.DeepClone() : null;
        }

        JsonNode? value = rule switch
        {
            PathRule p => JsonPathEvaluator.First(p.Path, body, headers)?.DeepClone(),
            HeaderRule h => headers[h.Header]?.DeepClone() ?? headers.FirstOrDefault(kv => string.Equals(kv.Key, h.Header, StringComparison.OrdinalIgnoreCase)).Value?.DeepClone(),
            ConstRule c => c.Value?.DeepClone(),
            FirstNonEmptyRule f => f.Candidates.Select(c => EvaluateRule(c, field, body, headers, fields)).FirstOrDefault(v => !ValueCoercion.IsEmpty(v)),
            LookupRule l => EvaluateLookup(l, body, headers, fields),
            TemplateRule t => JsonValue.Create(TemplateRenderer.Render(t.Template, r => Resolve(r, body, headers, fields))),
            ObjectRule o => EvaluateObject(o, field, body, headers, fields),
            PickRule pk => EvaluatePick(pk, body, headers),
            ArrayRule a => EvaluateArray(a, field, body, headers, fields),
            _ => null,
        };

        if (ValueCoercion.IsEmpty(value) && rule.HasDefault)
        {
            value = rule.Default?.DeepClone();
        }
        if (ValueCoercion.IsEmpty(value)) return null;

        if (rule.Transform.Count > 0 && value is JsonValue)
        {
            value = JsonValue.Create(Transforms.Apply(ValueCoercion.AsDisplayString(value), rule.Transform));
        }
        if (rule.As is { } type)
        {
            value = ValueCoercion.Coerce(value, type, field);
        }
        if (rule.MaxLength is { } max && value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String)
        {
            var s = sv.GetValue<string>();
            if (s.Length > max) value = JsonValue.Create(s[..max]);
        }
        return value;
    }

    private static JsonNode? EvaluateLookup(LookupRule rule, JsonNode? body, JsonObject headers, Dictionary<string, JsonNode?> fields)
    {
        var from = Resolve(rule.From, body, headers, fields);
        var key = ValueCoercion.AsDisplayString(from);
        if (!ValueCoercion.IsEmpty(from))
        {
            if (rule.Map is not null && rule.Map.TryGetValue(key, out var mapped)) return mapped?.DeepClone();
            if (rule.RegexMap is not null)
            {
                foreach (var (pattern, value) in rule.RegexMap)
                {
                    if (pattern.IsMatch(key)) return value?.DeepClone();
                }
            }
        }
        return rule.HasLookupDefault ? rule.LookupDefault?.DeepClone() : null;
    }

    private static JsonNode EvaluateObject(ObjectRule rule, string field, JsonNode? body, JsonObject headers, Dictionary<string, JsonNode?> fields)
    {
        var obj = new JsonObject();
        foreach (var (name, member) in rule.Members)
        {
            var v = EvaluateRule(member, $"{field}.{name}", body, headers, fields);
            if (!ValueCoercion.IsEmpty(v)) obj[name] = v;
        }
        return obj;
    }

    private static JsonNode EvaluatePick(PickRule rule, JsonNode? body, JsonObject headers)
    {
        var obj = new JsonObject();
        foreach (var path in rule.Paths)
        {
            var v = JsonPathEvaluator.First(path, body, headers);
            if (ValueCoercion.IsEmpty(v)) continue;
            var key = LastSegment(path);
            obj[key] = v!.DeepClone();
        }
        return obj;
    }

    private static JsonNode? EvaluateArray(ArrayRule rule, string field, JsonNode? body, JsonObject headers, Dictionary<string, JsonNode?> fields)
    {
        var items = JsonPathEvaluator.Evaluate(rule.From, body, headers);
        var results = new List<JsonNode?>();
        foreach (var item in items)
        {
            // `each` is evaluated with the item as the body root, so `$.name` addresses the element.
            var v = EvaluateRule(rule.Each, field, item, headers, fields);
            if (!ValueCoercion.IsEmpty(v)) results.Add(v);
        }
        if (rule.Join is not null)
        {
            return JsonValue.Create(string.Join(rule.Join, results.Select(ValueCoercion.AsDisplayString)));
        }
        return new JsonArray(results.ToArray());
    }

    private static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd(']');
        var lastDot = trimmed.LastIndexOf('.');
        var lastBracket = trimmed.LastIndexOf('[');
        var start = Math.Max(lastDot, lastBracket) + 1;
        return trimmed[start..].Trim('\'', '"');
    }
}
