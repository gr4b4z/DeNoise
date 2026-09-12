using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Policies;

namespace DeNoise.Application.Policies;

/// <summary>One policy in a bundle: what the import would do with it.</summary>
public sealed record ConfigImportEntry(string Kind, Guid PolicyId, string? Name, string Action, int? Version, IReadOnlyList<string> Changes);

/// <summary>Outcome of an import (dry run or applied): per-policy actions plus document-level errors (06 §8: "returns a structured diff before activation").</summary>
public sealed record ConfigImportResult(bool DryRun, IReadOnlyList<ConfigImportEntry> Entries, IReadOnlyList<string> Errors)
{
    public bool HasChanges => Entries.Any(e => e.Action != ConfigImportActions.Unchanged);
}

public static class ConfigImportActions
{
    public const string Unchanged = "unchanged";
    public const string NewVersion = "new_version";
    public const string Create = "create";
}

/// <summary>
/// Config-as-code for policies (06 §8, 09 M10): <c>export</c> writes every active policy of every kind as one YAML bundle;
/// <c>import</c> reads the same shape, compares each document with the active version and — unless <c>dryRun</c> — creates and
/// activates a new version where they differ. Import never deletes and never touches versions it does not mention.
/// </summary>
public sealed class ConfigBundleService(IPolicyRepository policies, PolicyService service)
{
    public const int BundleVersion = 1;

    public async Task<string> ExportAsync(CancellationToken ct = default)
    {
        var root = new JsonObject { ["denoise_config"] = BundleVersion, ["policies"] = new JsonObject() };
        var section = (JsonObject)root["policies"]!;
        foreach (var kind in PolicyKinds.All)
        {
            var list = new JsonArray();
            foreach (var version in (await policies.ListActiveAsync(kind, ct)).OrderBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.PolicyId))
            {
                var entry = new JsonObject { ["id"] = version.PolicyId.ToString() };
                if (version.Name is { Length: > 0 }) entry["name"] = version.Name;
                entry["version"] = version.Version;
                entry["document"] = JsonNode.Parse(version.Body) ?? new JsonObject();
                list.Add(entry);
            }
            section[kind] = list;
        }
        return "# DeNoise policies — export of every active version; import with POST /api/v1/config/import\n" + YamlJson.ToYaml(root);
    }

    public async Task<ConfigImportResult> ImportAsync(string yaml, bool dryRun, Actor actor, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var entries = new List<ConfigImportEntry>();
        JsonObject? root;
        try
        {
            root = YamlJson.Parse(yaml) as JsonObject;
        }
        catch (Exception ex) when (ex is MappingValidationException or YamlDotNet.Core.YamlException)
        {
            return new ConfigImportResult(dryRun, [], [$"document does not parse: {ex.Message}"]);
        }
        if (root is null || root["policies"] is not JsonObject section)
        {
            return new ConfigImportResult(dryRun, [], ["document must contain a 'policies' object (as written by the export)"]);
        }
        foreach (var (kind, node) in section)
        {
            if (!PolicyKinds.All.Contains(kind))
            {
                errors.Add($"policies.{kind}: unknown policy kind");
                continue;
            }
            if (node is not JsonArray list)
            {
                errors.Add($"policies.{kind}: must be a list");
                continue;
            }
            for (var i = 0; i < list.Count; i++)
            {
                var path = $"policies.{kind}[{i}]";
                if (list[i] is not JsonObject item || item["document"] is not JsonObject document)
                {
                    errors.Add($"{path}: must be an object with a 'document'");
                    continue;
                }
                var name = item["name"]?.ToString();
                Guid policyId;
                if (item["id"] is JsonValue idv && Guid.TryParse(idv.ToString(), out var parsed)) policyId = parsed;
                else if (kind == PolicyKinds.Routing) policyId = WellKnownPolicies.Routing;
                else if (kind == PolicyKinds.Grouping) policyId = WellKnownPolicies.Grouping;
                else
                {
                    errors.Add($"{path}: escalation and lifecycle policies need an 'id' (uuid) so re-imports address the same policy");
                    continue;
                }
                var active = await policies.GetActiveAsync(kind, policyId, ct);
                var incoming = Canonical(document);
                if (active is not null && Canonical(JsonNode.Parse(active.Body)) == incoming)
                {
                    entries.Add(new ConfigImportEntry(kind, policyId, name ?? active.Name, ConfigImportActions.Unchanged, active.Version, []));
                    continue;
                }
                var changes = Changes(active is null ? null : JsonNode.Parse(active.Body) as JsonObject, document);
                var action = active is null ? ConfigImportActions.Create : ConfigImportActions.NewVersion;
                if (dryRun)
                {
                    entries.Add(new ConfigImportEntry(kind, policyId, name ?? active?.Name, action, null, changes));
                    continue;
                }
                try
                {
                    var created = await service.CreateVersionAsync(new CreatePolicyVersion(kind, YamlJson.ToYaml(document), policyId, name ?? active?.Name), actor, ct);
                    await service.ActivateAsync(kind, policyId, created.Version, actor, ct);
                    entries.Add(new ConfigImportEntry(kind, policyId, created.Name, action, created.Version, changes));
                }
                catch (Exception ex) when (ex is MappingValidationException or ArgumentException or NotSupportedException or KeyNotFoundException)
                {
                    errors.Add($"{path}: {ex.Message}");
                }
            }
        }
        return new ConfigImportResult(dryRun, entries, errors);
    }

    /// <summary>Top-level field diff: enough for a review table; the full YAML diff is the editor's job.</summary>
    private static List<string> Changes(JsonObject? before, JsonObject after)
    {
        var changes = new List<string>();
        if (before is null)
        {
            changes.Add("new policy");
            return changes;
        }
        foreach (var key in before.Select(kv => kv.Key).Union(after.Select(kv => kv.Key), StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal))
        {
            var b = before[key];
            var a = after[key];
            if (b is null && a is not null) changes.Add($"+ {key}");
            else if (b is not null && a is null) changes.Add($"- {key}");
            else if (Canonical(b) != Canonical(a)) changes.Add($"~ {key}");
        }
        return changes;
    }

    private static string Canonical(JsonNode? node)
    {
        if (node is null) return "null";
        var normalised = Normalise(node);
        return normalised?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
    }

    private static JsonNode? Normalise(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Normalise(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Normalise).ToArray()),
        JsonValue value => value.DeepClone(),
        _ => null,
    };
}
