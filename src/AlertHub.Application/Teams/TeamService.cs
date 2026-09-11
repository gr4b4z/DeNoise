using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Teams;

namespace AlertHub.Application.Teams;

public interface ITeamRepository
{
    Task<Team?> GetAsync(Guid teamId, CancellationToken ct = default);
    Task<IReadOnlyList<Team>> ListAsync(CancellationToken ct = default);
    Task<Team?> GetTriageAsync(CancellationToken ct = default);
    void Add(Team team);
}

public sealed record CreateTeam(string Name, string[] AccessScopes, bool IsTriage = false, Guid? FallbackTeamId = null, Guid? DefaultEscalationPolicyId = null, string? CoverageHours = null);

public sealed class TeamService(ITeamRepository teams, IAuditWriter audit, IUnitOfWork uow, TimeProvider time)
{
    public async Task<Team> CreateAsync(CreateTeam request, Actor actor, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("name is required", nameof(request));
        if (request.IsTriage && await teams.GetTriageAsync(ct) is { } existing)
        {
            throw new InvalidOperationException($"Team '{existing.Name}' is already the triage team.");
        }
        var now = time.GetUtcNow();
        var team = new Team
        {
            TeamId = Ids.New(time),
            Name = request.Name.Trim(),
            AccessScopes = request.AccessScopes,
            IsTriage = request.IsTriage,
            FallbackTeamId = request.FallbackTeamId,
            DefaultEscalationPolicyId = request.DefaultEscalationPolicyId,
            CoverageHours = request.CoverageHours,
            CreatedAt = now,
            UpdatedAt = now,
        };
        teams.Add(team);
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "team.create",
            TargetType = "team",
            TargetId = team.TeamId.ToString(),
            After = JsonSerializer.Serialize(new { team.Name, team.AccessScopes, team.IsTriage }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
        await uow.CommitAsync(ct);
        return team;
    }
}
