using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Application.Auth;
using DeNoise.Application.Integrations;
using DeNoise.Application.Mapping;
using DeNoise.Application.Processing;
using DeNoise.Application.Teams;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;
using DeNoise.Domain.Heartbeats;
using DeNoise.Domain.Integrations;
using DeNoise.Domain.Teams;
using DeNoise.Domain.Users;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Heartbeats;

public sealed record HeartbeatDefinition(
    string Name, string? Description, Guid OwningTeamId, Guid? AssigneeId, string ScheduleKind, TimeSpan? Interval, string? Cron, string? Timezone,
    TimeSpan Grace, string SeverityOnMiss, Guid? RoutingPolicyId, Guid? BindsToIntegrationId, int RecoverySuccessesRequired = 1, bool AutoPauseDuringMaintenance = true, string? AccessScope = null);

public sealed record HeartbeatCreated(Heartbeat Heartbeat, string PingUrl);

public sealed record HeartbeatImportEntry(string Name, string Action, Guid? HeartbeatId, IReadOnlyList<string> Changes, string? PingUrl);
public sealed record HeartbeatImportResult(bool DryRun, IReadOnlyList<HeartbeatImportEntry> Entries, IReadOnlyList<string> Errors);

/// <summary>
/// Management of registered heartbeats (06 §4, spec §13.3.2): create with a one-time ping URL, update with optimistic concurrency,
/// pause/resume with actor and reason, token rotation that keeps identity and history, YAML export/import for config-as-code.
/// </summary>
public sealed class HeartbeatService(
    IHeartbeatRepository heartbeats, ITeamRepository teams, IIntegrationRepository integrations, IProcessingUnitOfWork processing, HeartbeatEffects effects,
    ISecretHasher hasher, IAuditWriter audit, IUnitOfWork uow, IOptions<HeartbeatOptions> options, TimeProvider time)
{
    public Task<Heartbeat?> GetAsync(Guid id, CancellationToken ct = default) => heartbeats.GetAsync(id, ct);
    public Task<IReadOnlyList<Heartbeat>> ListAsync(HeartbeatFilter filter, CancellationToken ct = default) => heartbeats.ListAsync(filter, ct);
    public Task<IReadOnlyList<HeartbeatRun>> RunsAsync(Guid id, int limit, CancellationToken ct = default) => heartbeats.ListRunsAsync(id, limit, ct);

    public async Task<HeartbeatCreated> CreateAsync(HeartbeatDefinition definition, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var team = await ValidateAsync(definition, ct);
        var scope = definition.AccessScope ?? team.AccessScopes.FirstOrDefault() ?? throw new ArgumentException("the team has no access scope; pass accessScope explicitly");
        if (!actor.CanSeeScope(scope)) throw new ForbiddenException(Permissions.HeartbeatManage, $"scope {scope} is outside your access");
        await EnsureSystemIntegrationAsync(ct);

        var now = time.GetUtcNow();
        var secret = TokenGenerator.NewSecret();
        var hb = new Heartbeat
        {
            HeartbeatId = Ids.New(time),
            Name = definition.Name.Trim(),
            Description = definition.Description,
            AccessScope = scope,
            OwningTeamId = definition.OwningTeamId,
            AssigneeId = definition.AssigneeId,
            ScheduleKind = definition.ScheduleKind,
            Interval = definition.Interval,
            Cron = definition.Cron,
            ScheduleTz = definition.ScheduleKind == ScheduleKinds.Cron ? definition.Timezone : null,
            Grace = definition.Grace,
            SeverityOnMiss = definition.SeverityOnMiss,
            RoutingPolicyId = definition.RoutingPolicyId,
            BindsToIntegrationId = definition.BindsToIntegrationId,
            RecoverySuccessesRequired = Math.Max(1, definition.RecoverySuccessesRequired),
            AutoPauseDuringMaintenance = definition.AutoPauseDuringMaintenance,
            State = HeartbeatStates.Unknown,
            KeyId = TokenGenerator.NewKeyId(),
            TokenHash = hasher.HashToken(secret),
            TokenRotatedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        // Until the first ping the deadline counts from registration, so a job that never starts is still caught (spec §13.3.3).
        hb.ExpectedNext = HeartbeatSchedule.NextAfter(hb, now);
        hb.State = HeartbeatStates.Healthy;
        heartbeats.Add(hb);
        Audit(actor, "heartbeat.create", hb, correlationId, null, new { hb.Name, hb.ScheduleKind, interval = hb.Interval?.ToString(), hb.Cron, hb.ScheduleTz, grace = hb.Grace.ToString(), hb.SeverityOnMiss, hb.BindsToIntegrationId });
        await uow.CommitAsync(ct);
        return new HeartbeatCreated(hb, PingUrl(hb.KeyId, secret));
    }

    public async Task<Heartbeat> UpdateAsync(Guid id, int expectedVersion, HeartbeatDefinition definition, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var hb = await RequireAsync(id, actor, ct);
        if (hb.Version != expectedVersion) throw new Episodes.VersionConflictException(id, hb.Version);
        var team = await ValidateAsync(definition, ct);
        var before = Snapshot(hb);
        var now = time.GetUtcNow();
        hb.Name = definition.Name.Trim();
        hb.Description = definition.Description;
        hb.OwningTeamId = definition.OwningTeamId;
        hb.AssigneeId = definition.AssigneeId;
        hb.AccessScope = definition.AccessScope ?? team.AccessScopes.FirstOrDefault() ?? hb.AccessScope;
        var scheduleChanged = hb.ScheduleKind != definition.ScheduleKind || hb.Interval != definition.Interval || hb.Cron != definition.Cron || hb.ScheduleTz != definition.Timezone;
        hb.ScheduleKind = definition.ScheduleKind;
        hb.Interval = definition.Interval;
        hb.Cron = definition.Cron;
        hb.ScheduleTz = definition.ScheduleKind == ScheduleKinds.Cron ? definition.Timezone : null;
        hb.Grace = definition.Grace;
        hb.SeverityOnMiss = definition.SeverityOnMiss;
        hb.RoutingPolicyId = definition.RoutingPolicyId;
        hb.BindsToIntegrationId = definition.BindsToIntegrationId;
        hb.RecoverySuccessesRequired = Math.Max(1, definition.RecoverySuccessesRequired);
        hb.AutoPauseDuringMaintenance = definition.AutoPauseDuringMaintenance;
        if (scheduleChanged && !hb.IsPaused) hb.ExpectedNext = HeartbeatSchedule.NextAfter(hb, hb.LastPingAt ?? now);
        hb.Touch(now);
        Audit(actor, "heartbeat.update", hb, correlationId, before, Snapshot(hb));
        await uow.CommitAsync(ct);
        return hb;
    }

    public async Task DeleteAsync(Guid id, int expectedVersion, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var hb = await RequireAsync(id, actor, ct);
        if (hb.Version != expectedVersion) throw new Episodes.VersionConflictException(id, hb.Version);
        if (hb.MissEpisodeId is { } missId)
        {
            await processing.RunAsync<object?>(async session =>
            {
                if (await session.FindEpisodeAsync(missId, ct) is { IsOpen: true } miss)
                {
                    miss.CloseManually("heartbeat deleted", time.GetUtcNow());
                }
                return null;
            }, ct);
        }
        heartbeats.Remove(hb);
        Audit(actor, "heartbeat.delete", hb, correlationId, Snapshot(hb), null);
        await uow.CommitAsync(ct);
    }

    public async Task<Heartbeat> PauseAsync(Guid id, int expectedVersion, string reason, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a reason is required to pause a heartbeat");
        var hb = await RequireAsync(id, actor, ct);
        if (hb.Version != expectedVersion) throw new Episodes.VersionConflictException(id, hb.Version);
        var outcome = await processing.RunAsync(async session =>
        {
            var locked = await session.FindHeartbeatForUpdateAsync(id, ct) ?? throw new KeyNotFoundException("heartbeat vanished");
            var now = time.GetUtcNow();
            var transition = HeartbeatMachine.Pause(locked, actor.UserId, reason.Trim(), now);
            var episodes = await effects.ApplyAsync(locked, transition, session, now, reason.Trim(), ct);
            session.AddAudit(AuditEntry(actor, "heartbeat.pause", locked, correlationId, null, new { reason = reason.Trim() }, now));
            return new HeartbeatOutcome(locked, transition, episodes);
        }, ct);
        await effects.AnnounceAsync(outcome.Heartbeat, outcome.Episodes, ct);
        return outcome.Heartbeat;
    }

    public async Task<Heartbeat> ResumeAsync(Guid id, int expectedVersion, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var hb = await RequireAsync(id, actor, ct);
        if (hb.Version != expectedVersion) throw new Episodes.VersionConflictException(id, hb.Version);
        var outcome = await processing.RunAsync(async session =>
        {
            var locked = await session.FindHeartbeatForUpdateAsync(id, ct) ?? throw new KeyNotFoundException("heartbeat vanished");
            var now = time.GetUtcNow();
            var transition = HeartbeatMachine.Resume(locked, now);
            var episodes = await effects.ApplyAsync(locked, transition, session, now, "resumed by operator", ct);
            session.AddAudit(AuditEntry(actor, "heartbeat.resume", locked, correlationId, null, new { expectedNext = locked.ExpectedNext }, now));
            return new HeartbeatOutcome(locked, transition, episodes);
        }, ct);
        await effects.AnnounceAsync(outcome.Heartbeat, outcome.Episodes, ct);
        return outcome.Heartbeat;
    }

    /// <summary>New secret, same key id, same identity, history and state (spec §22 "Heartbeat token is rotated"). The old token stops working at commit.</summary>
    public async Task<HeartbeatCreated> RotateTokenAsync(Guid id, int expectedVersion, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var hb = await RequireAsync(id, actor, ct);
        if (hb.Version != expectedVersion) throw new Episodes.VersionConflictException(id, hb.Version);
        var now = time.GetUtcNow();
        var secret = TokenGenerator.NewSecret();
        hb.TokenHash = hasher.HashToken(secret);
        hb.TokenRotatedAt = now;
        hb.Touch(now);
        Audit(actor, "heartbeat.rotate_token", hb, correlationId, null, new { rotatedAt = now });
        await uow.CommitAsync(ct);
        return new HeartbeatCreated(hb, PingUrl(hb.KeyId, secret));
    }

    // ---- YAML (config-as-code, spec §13.3.2) ------------------------------------------------------------------------

    public async Task<string> ExportAsync(Guid? teamId, DeNoisePrincipal actor, CancellationToken ct = default)
    {
        var list = (await heartbeats.ListAsync(new HeartbeatFilter(TeamId: teamId), ct)).Where(h => actor.CanSeeScope(h.AccessScope)).OrderBy(h => h.Name, StringComparer.Ordinal);
        var sb = new StringBuilder("heartbeats:\n");
        foreach (var hb in list)
        {
            sb.Append("  - name: ").Append(Quote(hb.Name)).Append('\n');
            if (hb.Description is { Length: > 0 }) sb.Append("    description: ").Append(Quote(hb.Description)).Append('\n');
            sb.Append("    team: ").Append(hb.OwningTeamId).Append('\n');
            sb.Append("    access_scope: ").Append(Quote(hb.AccessScope)).Append('\n');
            sb.Append("    schedule:\n");
            if (hb.ScheduleKind == ScheduleKinds.Interval) sb.Append("      interval: ").Append(Duration(hb.Interval ?? TimeSpan.Zero)).Append('\n');
            else sb.Append("      cron: ").Append(Quote(hb.Cron ?? string.Empty)).Append("\n      timezone: ").Append(Quote(hb.ScheduleTz ?? "UTC")).Append('\n');
            sb.Append("    grace: ").Append(Duration(hb.Grace)).Append('\n');
            sb.Append("    severity_on_miss: ").Append(hb.SeverityOnMiss).Append('\n');
            if (hb.BindsToIntegrationId is { } b) sb.Append("    binds_to_integration: ").Append(b).Append('\n');
            if (hb.RoutingPolicyId is { } r) sb.Append("    routing_policy: ").Append(r).Append('\n');
            sb.Append("    recovery_successes_required: ").Append(hb.RecoverySuccessesRequired).Append('\n');
            sb.Append("    auto_pause_during_maintenance: ").Append(hb.AutoPauseDuringMaintenance ? "true" : "false").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Import by name within a team: creates missing heartbeats (ping URL shown once), updates changed ones, never deletes. <c>dryRun</c> returns the diff only.</summary>
    public async Task<HeartbeatImportResult> ImportAsync(string yaml, bool dryRun, DeNoisePrincipal actor, string correlationId, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var entries = new List<HeartbeatImportEntry>();
        var root = YamlJson.Parse(yaml) as JsonObject;
        if (root?["heartbeats"] is not JsonArray list)
        {
            return new HeartbeatImportResult(dryRun, [], ["document must contain a 'heartbeats' list"]);
        }
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject item)
            {
                errors.Add($"heartbeats[{i}]: must be an object");
                continue;
            }
            HeartbeatDefinition definition;
            try
            {
                definition = ParseDefinition(item, $"heartbeats[{i}]");
            }
            catch (ArgumentException ex)
            {
                errors.Add(ex.Message);
                continue;
            }
            var existing = (await heartbeats.ListAsync(new HeartbeatFilter(TeamId: definition.OwningTeamId, Query: definition.Name), ct)).FirstOrDefault(h => string.Equals(h.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                if (dryRun)
                {
                    entries.Add(new HeartbeatImportEntry(definition.Name, "create", null, ["new heartbeat"], null));
                    continue;
                }
                try
                {
                    var created = await CreateAsync(definition, actor, correlationId, ct);
                    entries.Add(new HeartbeatImportEntry(definition.Name, "create", created.Heartbeat.HeartbeatId, ["created"], created.PingUrl));
                }
                catch (Exception ex) when (ex is ArgumentException or MappingValidationException or KeyNotFoundException)
                {
                    errors.Add($"{definition.Name}: {ex.Message}");
                }
                continue;
            }
            var changes = Diff(existing, definition);
            if (changes.Count == 0)
            {
                entries.Add(new HeartbeatImportEntry(definition.Name, "unchanged", existing.HeartbeatId, [], null));
                continue;
            }
            if (!dryRun)
            {
                try
                {
                    await UpdateAsync(existing.HeartbeatId, existing.Version, definition, actor, correlationId, ct);
                }
                catch (Exception ex) when (ex is ArgumentException or MappingValidationException or KeyNotFoundException)
                {
                    errors.Add($"{definition.Name}: {ex.Message}");
                    continue;
                }
            }
            entries.Add(new HeartbeatImportEntry(definition.Name, "update", existing.HeartbeatId, changes, null));
        }
        return new HeartbeatImportResult(dryRun, entries, errors);
    }

    public static HeartbeatDefinition ParseDefinition(JsonObject item, string path)
    {
        var name = item["name"]?.ToString() ?? throw new ArgumentException($"{path}.name is required");
        if (!Guid.TryParse(item["team"]?.ToString(), out var team)) throw new ArgumentException($"{path}.team must be a team id (uuid)");
        var schedule = item["schedule"] as JsonObject ?? throw new ArgumentException($"{path}.schedule is required");
        var errors = new List<MappingValidationError>();
        var interval = Routing.EscalationPolicyDocument.ParseDuration(schedule["interval"], $"{path}.schedule.interval", errors);
        var cron = schedule["cron"]?.ToString();
        var kind = cron is { Length: > 0 } ? ScheduleKinds.Cron : ScheduleKinds.Interval;
        var grace = Routing.EscalationPolicyDocument.ParseDuration(item["grace"], $"{path}.grace", errors) ?? TimeSpan.FromMinutes(5);
        if (errors.Count > 0) throw new ArgumentException(string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        Guid? binds = Guid.TryParse(item["binds_to_integration"]?.ToString(), out var b) ? b : null;
        Guid? routing = Guid.TryParse(item["routing_policy"]?.ToString(), out var r) ? r : null;
        Guid? assignee = Guid.TryParse(item["assignee"]?.ToString(), out var a) ? a : null;
        var recovery = item["recovery_successes_required"] is JsonValue rv && MappingParser.TryGetInt(rv, out var rec) ? rec : 1;
        var autoPause = item["auto_pause_during_maintenance"] is not JsonValue ap || !ap.TryGetValue<bool>(out var apv) || apv;
        return new HeartbeatDefinition(name, item["description"]?.ToString(), team, assignee, kind, interval, cron, schedule["timezone"]?.ToString(), grace,
            item["severity_on_miss"]?.ToString()?.ToLowerInvariant() ?? "high", routing, binds, recovery, autoPause, item["access_scope"]?.ToString());
    }

    private static List<string> Diff(Heartbeat existing, HeartbeatDefinition d)
    {
        var changes = new List<string>();
        void Check<T>(string field, T before, T after)
        {
            if (!EqualityComparer<T>.Default.Equals(before, after)) changes.Add($"{field}: {before?.ToString() ?? "none"} → {after?.ToString() ?? "none"}");
        }
        Check("description", existing.Description, d.Description);
        Check("schedule.kind", existing.ScheduleKind, d.ScheduleKind);
        Check("schedule.interval", existing.Interval, d.Interval);
        Check("schedule.cron", existing.Cron, d.Cron);
        Check("schedule.timezone", existing.ScheduleTz, d.ScheduleKind == ScheduleKinds.Cron ? d.Timezone : null);
        Check("grace", existing.Grace, d.Grace);
        Check("severity_on_miss", existing.SeverityOnMiss, d.SeverityOnMiss);
        Check("binds_to_integration", existing.BindsToIntegrationId, d.BindsToIntegrationId);
        Check("routing_policy", existing.RoutingPolicyId, d.RoutingPolicyId);
        Check("recovery_successes_required", existing.RecoverySuccessesRequired, Math.Max(1, d.RecoverySuccessesRequired));
        Check("auto_pause_during_maintenance", existing.AutoPauseDuringMaintenance, d.AutoPauseDuringMaintenance);
        if (d.AccessScope is not null) Check("access_scope", existing.AccessScope, d.AccessScope);
        return changes;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private async Task<Team> ValidateAsync(HeartbeatDefinition d, CancellationToken ct)
    {
        var errors = new List<MappingValidationError>();
        if (string.IsNullOrWhiteSpace(d.Name)) errors.Add(new MappingValidationError("$.name", "required"));
        HeartbeatSchedule.Validate(d.ScheduleKind, d.Interval, d.Cron, d.Timezone, d.Grace, errors);
        var severity = SeverityExtensions.ParseWire(d.SeverityOnMiss);
        if (severity == Severity.Unknown && !string.Equals(d.SeverityOnMiss, "unknown", StringComparison.OrdinalIgnoreCase)) errors.Add(new MappingValidationError("$.severityOnMiss", $"unknown severity '{d.SeverityOnMiss}'"));
        var team = await teams.GetAsync(d.OwningTeamId, ct);
        if (team is null) errors.Add(new MappingValidationError("$.owningTeamId", "unknown team"));
        if (d.BindsToIntegrationId is { } b && await integrations.GetCurrentAsync(b, ct) is null) errors.Add(new MappingValidationError("$.bindsToIntegrationId", "unknown integration"));
        if (errors.Count > 0) throw new MappingValidationException(errors);
        return team!;
    }

    private async Task<Heartbeat> RequireAsync(Guid id, DeNoisePrincipal actor, CancellationToken ct)
    {
        var hb = await heartbeats.GetAsync(id, ct);
        if (hb is null || !actor.CanSeeScope(hb.AccessScope)) throw new OutOfScopeException("heartbeat", id);
        return hb;
    }

    /// <summary>Miss episodes need an integration row; heartbeats not bound to a source get this inactive system integration (cannot ingest).</summary>
    private async Task EnsureSystemIntegrationAsync(CancellationToken ct)
    {
        if (await integrations.GetCurrentAsync(WellKnownIntegrations.Heartbeats, ct) is not null) return;
        var now = time.GetUtcNow();
        integrations.Add(new Integration
        {
            IntegrationId = WellKnownIntegrations.Heartbeats,
            Name = "denoise-heartbeats",
            Type = IntegrationTypes.GenericWebhook,
            AccessScope = "denoise",
            IngestKeyId = TokenGenerator.NewKeyId(),
            IngestTokenHash = hasher.HashToken(TokenGenerator.NewSecret()),
            Active = false,
            ActivatedAt = now,
            CreatedBy = "system",
            CreatedAt = now,
        });
    }

    public string PingUrl(string keyId, string secret) => $"{options.Value.IngestPublicBaseUrl.TrimEnd('/')}/hb/{keyId}.{secret}";

    private static object Snapshot(Heartbeat hb) => new
    {
        hb.Name,
        hb.Description,
        hb.OwningTeamId,
        hb.AssigneeId,
        hb.AccessScope,
        hb.ScheduleKind,
        interval = hb.Interval?.ToString(),
        hb.Cron,
        hb.ScheduleTz,
        grace = hb.Grace.ToString(),
        hb.SeverityOnMiss,
        hb.RoutingPolicyId,
        hb.BindsToIntegrationId,
        hb.RecoverySuccessesRequired,
        hb.AutoPauseDuringMaintenance,
        hb.State,
    };

    private void Audit(DeNoisePrincipal actor, string action, Heartbeat hb, string correlationId, object? before, object? after)
        => audit.Record(AuditEntry(actor, action, hb, correlationId, before, after, time.GetUtcNow()));

    private static AuditEntry AuditEntry(DeNoisePrincipal actor, string action, Heartbeat hb, string correlationId, object? before, object? after, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        At = now,
        ActorType = ActorTypes.User,
        ActorId = actor.UserId.ToString(),
        ActorDisplay = actor.Username,
        Action = action,
        TargetType = "heartbeat",
        TargetId = hb.HeartbeatId.ToString(),
        AccessScope = hb.AccessScope,
        Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
        After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
        CorrelationId = correlationId,
    };

    private static string Duration(TimeSpan span)
        => span.TotalSeconds % 86400 == 0 ? $"{span.TotalDays:F0}d" : span.TotalSeconds % 3600 == 0 ? $"{span.TotalHours:F0}h" : span.TotalSeconds % 60 == 0 ? $"{span.TotalMinutes:F0}m" : $"{span.TotalSeconds:F0}s";

    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
