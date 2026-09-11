using AlertHub.Domain.Audit;

namespace AlertHub.Application.Audit;

/// <summary>Stages an audit row in the ambient unit of work; it is committed together with the change (AGENTS.md rule 4).</summary>
public interface IAuditWriter
{
    void Record(AuditEntry entry);
}
