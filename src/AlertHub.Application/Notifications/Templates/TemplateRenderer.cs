using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlertHub.Application.Mapping;
using AlertHub.Domain.Notifications;
using Fluid;

namespace AlertHub.Application.Notifications.Templates;

public sealed record RenderedBody(string Body, string ContentType);

public sealed class TemplateRenderException(string message) : Exception(message);

/// <summary>
/// Fluid (Liquid) in the ADR-7 sandbox: only the notification model is reachable (plain dictionaries and lists, no CLR members),
/// default filters only (none touch I/O), 200 ms render budget, 256 KiB output cap, loop step limit. <c>json</c> templates encode every
/// <c>{{ }}</c> value with the JSON escaper and the result must parse; <c>text</c> templates render verbatim (<c>escape</c> is available).
/// </summary>
public sealed class TemplateRenderer
{
    public static readonly TimeSpan RenderTimeout = TimeSpan.FromMilliseconds(200);
    public const int MaxOutputBytes = 256 * 1024;

    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = new() { MaxSteps = 20_000, MaxRecursion = 32 };
    private readonly ConcurrentDictionary<string, IFluidTemplate> _cache = new(StringComparer.Ordinal);

    /// <summary>Parses without rendering; errors carry the Fluid message (line/column included).</summary>
    public static void Validate(string format, string body, List<MappingValidationError> errors)
    {
        if (!TemplateFormats.All.Contains(format)) errors.Add(new MappingValidationError("$.format", $"must be {TemplateFormats.Json} or {TemplateFormats.Text}"));
        if (string.IsNullOrWhiteSpace(body)) errors.Add(new MappingValidationError("$.body", "required"));
        else if (!Parser.TryParse(body, out _, out var error)) errors.Add(new MappingValidationError("$.body", error));
    }

    public async Task<RenderedBody> RenderAsync(WebhookTemplate template, JsonObject model, CancellationToken ct = default)
    {
        // generic-json is the model itself (06 §7): no template pass, so it can never be broken by a field value.
        if (template.TemplateId == BuiltInTemplates.GenericJson) return new RenderedBody(model.ToJsonString(Abstractions.JsonDefaults.Stored), template.ContentType);
        return await RenderAsync(template.Format, template.Body, template.ContentType, model, $"{template.TemplateId}:{template.Version}", ct);
    }

    public async Task<RenderedBody> RenderAsync(string format, string body, string contentType, JsonObject model, string? cacheKey = null, CancellationToken ct = default)
    {
        var parsed = cacheKey is null ? Parse(body) : _cache.GetOrAdd(cacheKey, _ => Parse(body));
        var context = new TemplateContext(Options);
        foreach (var (key, value) in model) context.SetValue(key, ToPlain(value));
        var encoder = format == TemplateFormats.Json ? JavaScriptEncoder.UnsafeRelaxedJsonEscaping : (TextEncoder)NullEncoder.Default;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RenderTimeout);
        string output;
        try
        {
            var render = parsed.RenderAsync(context, encoder).AsTask();
            var finished = await Task.WhenAny(render, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
            if (finished != render) throw new TemplateRenderException($"render exceeded {RenderTimeout.TotalMilliseconds:F0} ms");
            output = await render;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TemplateRenderException($"render exceeded {RenderTimeout.TotalMilliseconds:F0} ms");
        }
        catch (Exception ex) when (ex is not TemplateRenderException and not OperationCanceledException)
        {
            throw new TemplateRenderException($"render failed: {ex.Message}");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(output) > MaxOutputBytes) throw new TemplateRenderException($"rendered body exceeds {MaxOutputBytes / 1024} KiB");
        if (format == TemplateFormats.Json)
        {
            try
            {
                using var _ = JsonDocument.Parse(output);
            }
            catch (JsonException ex)
            {
                throw new TemplateRenderException($"rendered body is not valid JSON: {ex.Message}");
            }
        }
        return new RenderedBody(output, contentType);
    }

    private static IFluidTemplate Parse(string body)
        => Parser.TryParse(body, out var template, out var error) ? template : throw new TemplateRenderException($"template does not parse: {error}");

    /// <summary>JSON → dictionaries, lists and primitives: the only shapes the sandbox exposes (no CLR members).</summary>
    public static object? ToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => ToPlain(kv.Value), StringComparer.Ordinal),
        JsonArray arr => arr.Select(ToPlain).ToList(),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => ToNumber(value),
            _ => null,
        },
        _ => node.ToJsonString(),
    };

    /// <summary>Numbers may wrap any CLR numeric or a <see cref="JsonElement"/>; normalise via the JSON text.</summary>
    private static object ToNumber(JsonValue value)
    {
        var element = value.Deserialize<JsonElement>();
        if (element.TryGetInt64(out var l)) return l;
        if (element.TryGetDecimal(out var d)) return d;
        return element.GetDouble();
    }
}
