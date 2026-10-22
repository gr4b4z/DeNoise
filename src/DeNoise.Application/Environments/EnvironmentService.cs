using System.Text.Json;
using System.Text.Json.Nodes;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Application.Mapping;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Environments;

namespace DeNoise.Application.Environments;

public interface IEnvironmentRepository
{
    /// <summary>Every entry, ordered by <c>sort_order</c> then key.</summary>
    Task<IReadOnlyList<EnvironmentClass>> ListAsync(CancellationToken ct = default);
    Task<EnvironmentClass?> GetTrackedAsync(string key, CancellationToken ct = default);
    void Add(EnvironmentClass environment);
    void Remove(EnvironmentClass environment);
}

public sealed record CreateEnvironment(string Key, string Label, int? SortOrder = null);

public sealed record UpdateEnvironment(string? Label, int? SortOrder);

/// <summary>Caller-supplied <c>If-Match</c> did not match the entry's version; the API answers 409 (06 §1).</summary>
public sealed class EnvironmentVersionConflictException(string key, int currentVersion) : Exception($"environment '{key}' is at version {currentVersion}")
{
    public string Key { get; } = key;
    public int CurrentVersion { get; } = currentVersion;
}

/// <summary>
/// The environment dictionary (ADR-17, spec §15.5). Changes are audited. A key is immutable: it is stored on every episode and
/// matched by routing, lifecycle and suppression predicates, so renaming means adding the new class, re-pointing the mappings and
/// deleting the old one.
/// </summary>
public sealed class EnvironmentService(IEnvironmentRepository environments, IMappingRepository mappings, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public Task<IReadOnlyList<EnvironmentClass>> ListAsync(CancellationToken ct = default) => environments.ListAsync(ct);

    public async Task<EnvironmentClass> CreateAsync(CreateEnvironment request, Actor actor, CancellationToken ct = default)
    {
        var key = request.Key?.Trim() ?? string.Empty;
        if (!EnvironmentClasses.IsValidKey(key))
        {
            throw new ArgumentException("key must be 1–32 characters: a lower-case letter, then lower-case letters, digits, '-' or '_'", nameof(request));
        }
        var label = ValidLabel(request.Label);
        var existing = await environments.ListAsync(ct);
        if (existing.Any(e => e.Key == key)) throw new InvalidOperationException($"Environment '{key}' already exists.");

        var now = time.GetUtcNow();
        var created = new EnvironmentClass
        {
            Key = key,
            Label = label,
            SortOrder = request.SortOrder ?? NextSortOrder(existing),
            CreatedAt = now,
            UpdatedAt = now,
        };
        environments.Add(created);
        Audit("environment.create", key, actor, now, before: null, after: new { created.Label, created.SortOrder });
        await uow.CommitAsync(ct);
        return created;
    }

    public async Task<EnvironmentClass> UpdateAsync(string key, int expectedVersion, UpdateEnvironment request, Actor actor, CancellationToken ct = default)
    {
        var entry = await environments.GetTrackedAsync(key, ct) ?? throw new KeyNotFoundException($"Environment '{key}' not found.");
        if (entry.Version != expectedVersion) throw new EnvironmentVersionConflictException(key, entry.Version);
        var before = new { entry.Label, entry.SortOrder };
        if (request.Label is not null) entry.Label = ValidLabel(request.Label);
        if (request.SortOrder is { } order) entry.SortOrder = order;
        var now = time.GetUtcNow();
        entry.Version++;
        entry.UpdatedAt = now;
        Audit("environment.update", key, actor, now, before, new { entry.Label, entry.SortOrder });
        await uow.CommitAsync(ct);
        return entry;
    }

    /// <summary>
    /// Refused for built-in entries and for a class an active mapping still produces (the mapping would no longer validate).
    /// Episodes already carrying the key keep it and from then on count as unclassified, so they move into the default view.
    /// </summary>
    public async Task DeleteAsync(string key, int expectedVersion, Actor actor, CancellationToken ct = default)
    {
        var entry = await environments.GetTrackedAsync(key, ct) ?? throw new KeyNotFoundException($"Environment '{key}' not found.");
        if (entry.Version != expectedVersion) throw new EnvironmentVersionConflictException(key, entry.Version);
        if (entry.IsBuiltIn) throw new InvalidOperationException($"Environment '{key}' is built in and cannot be deleted.");

        var users = new List<string>();
        foreach (var mapping in await mappings.ListActiveAsync(ct))
        {
            var doc = MappingParser.Parse(JsonNode.Parse(mapping.Body)!, mapping.Version);
            if (MappingEnvironments.Literals(doc).Any(l => l.Value == key)) users.Add(mapping.Name ?? mapping.MappingId.ToString());
        }
        if (users.Count > 0)
        {
            throw new InvalidOperationException($"Environment '{key}' is produced by active mapping(s) {string.Join(", ", users.Order(StringComparer.Ordinal))}; change them first.");
        }

        environments.Remove(entry);
        Audit("environment.delete", key, actor, time.GetUtcNow(), new { entry.Label, entry.SortOrder }, after: null);
        await uow.CommitAsync(ct);
    }

    private static string ValidLabel(string? label)
    {
        var trimmed = label?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > EnvironmentClasses.MaxLabelLength) throw new ArgumentException($"label must be 1–{EnvironmentClasses.MaxLabelLength} characters", nameof(label));
        return trimmed;
    }

    /// <summary>New classes go after the configured ones but before the built-in <c>unknown</c>, which stays last.</summary>
    private static int NextSortOrder(IReadOnlyList<EnvironmentClass> existing)
    {
        var configured = existing.Where(e => !e.IsBuiltIn).Select(e => e.SortOrder).DefaultIfEmpty(0).Max();
        return configured + 10;
    }

    private void Audit(string action, string key, Actor actor, DateTimeOffset now, object? before, object? after) => audit.Record(new AuditEntry
    {
        Id = Ids.New(time),
        At = now,
        ActorType = actor.Type,
        ActorId = actor.Id,
        ActorDisplay = actor.Display,
        Action = action,
        TargetType = "environment",
        TargetId = key,
        Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
        After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
        CorrelationId = actor.CorrelationId,
        RequestIp = actor.Ip,
    });
}
