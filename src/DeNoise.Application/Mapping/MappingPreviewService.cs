using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Policies;
using DeNoise.Application.Processing;
using DeNoise.Application.Routing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Policies;

namespace DeNoise.Application.Mapping;

public sealed record PreviewInput(string Label, JsonNode Body, IReadOnlyDictionary<string, string>? Headers);

public sealed record RoutingPreview(Guid? TeamId, string? TeamName, Guid? RuleId, string? RuleName, bool CorrectionRequired, string Why);

/// <summary>One previewed input: what the mapping would produce, without any state change (06 §4 <c>/preview</c>, spec §17.5 preview mode).</summary>
public sealed record MappingPreviewItem(string Source, bool Applies, bool Ok, string? Error, string? ErrorField,
    IReadOnlyDictionary<string, string?> Fields, IReadOnlyList<Domain.Alerts.IdentityComponent> Identity, string? Fingerprint, string? DeliveryKey,
    string? LifecycleProfileHint, RoutingPreview? Routing);

/// <summary>
/// Runs a stored mapping version or an unsaved draft against stored raw events or pasted bodies and reports fields, identity,
/// fingerprint and the routing outcome the active routing policy would give — the editor's live preview and the wizard's dry run.
/// </summary>
public sealed class MappingPreviewService(IMappingRepository mappings, IRawEventReader rawEvents, IPolicyRepository policies, ITeamRepository teams, TimeProvider time)
{
    public const int MaxInputs = 50;

    public async Task<MappingDocument> ResolveAsync(Guid mappingId, int version, CancellationToken ct)
    {
        var stored = await mappings.GetAsync(mappingId, version, ct) ?? throw new KeyNotFoundException($"Mapping {mappingId} v{version} not found.");
        return MappingParser.Parse(JsonNode.Parse(stored.Body)!, stored.Version);
    }

    public static MappingDocument Draft(string yaml) => MappingParser.ParseYaml(yaml, 0);

    /// <summary>Loads stored raw events by id (any partition) as preview inputs; unknown ids become failed items so the caller sees them.</summary>
    public async Task<IReadOnlyList<PreviewInput>> LoadRawAsync(Guid integrationId, IReadOnlyList<Guid> eventIds, CancellationToken ct)
    {
        var inputs = new List<PreviewInput>();
        foreach (var id in eventIds.Take(MaxInputs))
        {
            var raw = await rawEvents.FindAsync(integrationId, id, ct);
            if (raw is null)
            {
                inputs.Add(new PreviewInput($"raw:{id}", new JsonObject(), null));
                continue;
            }
            JsonNode body;
            try
            {
                body = JsonNode.Parse(raw.Body) ?? new JsonObject();
            }
            catch (JsonException)
            {
                body = new JsonObject { ["_raw"] = Encoding.UTF8.GetString(raw.Body) };
            }
            IReadOnlyDictionary<string, string>? headers = null;
            if (!string.IsNullOrEmpty(raw.Headers))
            {
                try
                {
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(raw.Headers, JsonDefaults.Stored);
                }
                catch (JsonException)
                {
                    headers = null;
                }
            }
            inputs.Add(new PreviewInput($"raw:{id}", body, headers));
        }
        return inputs;
    }

    public async Task<IReadOnlyList<MappingPreviewItem>> PreviewAsync(Integration integration, MappingDocument doc, IReadOnlyList<PreviewInput> inputs, bool includeRouting, CancellationToken ct)
    {
        RoutingPolicyDocument? routing = null;
        IReadOnlyList<Domain.Teams.Team> allTeams = [];
        if (includeRouting)
        {
            var active = await policies.GetActiveAsync(PolicyKinds.Routing, WellKnownPolicies.Routing, ct);
            if (active is not null)
            {
                try
                {
                    routing = RoutingPolicyDocument.Parse(JsonNode.Parse(active.Body)!, active.Version);
                }
                catch (MappingValidationException)
                {
                    routing = null;
                }
            }
            allTeams = await teams.ListAsync(ct);
        }
        var now = time.GetUtcNow();
        var items = new List<MappingPreviewItem>(inputs.Count);
        foreach (var input in inputs.Take(MaxInputs))
        {
            var headers = input.Headers ?? new Dictionary<string, string>();
            var raw = new RawInput(Encoding.UTF8.GetBytes(input.Body.ToJsonString()), headers);
            var applies = MappingEngine.AppliesTo(doc, raw.ParseBody(), raw.HeadersAsJson());
            try
            {
                var result = MappingEngine.Normalise(doc, raw, new MappingContext(integration.IntegrationId, Guid.NewGuid(), now, doc.Version));
                var fields = result.Fields.ToDictionary(kv => kv.Key, kv => (string?)ValueCoercion.AsDisplayString(kv.Value), StringComparer.Ordinal);
                RoutingPreview? route = null;
                if (includeRouting && result.Event.EventType is Domain.Alerts.EventTypes.Firing or Domain.Alerts.EventTypes.Update or Domain.Alerts.EventTypes.Informational)
                {
                    var episode = Episode.Open(result.Event, integration.AccessScope, now, time);
                    var triage = allTeams.FirstOrDefault(t => t.IsTriage);
                    var decision = RoutingEngine.Route(routing, result.Event, episode, integration, triage, allTeams.Select(t => t.TeamId).ToHashSet());
                    route = new RoutingPreview(decision.TeamId, allTeams.FirstOrDefault(t => t.TeamId == decision.TeamId)?.Name, decision.RuleId, decision.RuleName, decision.CorrectionRequired, decision.Why);
                }
                items.Add(new MappingPreviewItem(input.Label, applies, true, null, null, fields, result.Event.IdentityComponents ?? [], result.Event.Fingerprint, result.Event.DeliveryKey, result.Event.LifecycleProfileHint ?? doc.LifecycleProfileHint, route));
            }
            catch (MappingException ex)
            {
                items.Add(new MappingPreviewItem(input.Label, applies, false, ex.Message, ex.Field, new Dictionary<string, string?>(), [], null, null, doc.LifecycleProfileHint, null));
            }
        }
        return items;
    }
}
