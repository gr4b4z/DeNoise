namespace DeNoise.Domain.Common;

/// <summary>
/// Time-ordered identifiers (UUIDv7) generated in the application, per 05 §1.
/// The time source is injected so tests can produce deterministic ordering.
/// </summary>
public static class Ids
{
    public static Guid New(TimeProvider time) => Guid.CreateVersion7(time.GetUtcNow());
}
