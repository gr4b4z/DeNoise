using DeNoise.Domain.Alerts;
using DeNoise.Domain.Common;
using DeNoise.Domain.Episodes;

namespace DeNoise.Domain.Tests.Episodes;

/// <summary>Spec §9 severity table: <c>low</c> and <c>informational</c> are queue only; everything else, <c>unknown</c> included, notifies.</summary>
[Trait("Category", "Unit")]
public sealed class EpisodeQueueOnlyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private static NormalisedEvent Event(string type, Severity severity) => new()
    {
        EventId = Guid.NewGuid(),
        IntegrationId = Guid.NewGuid(),
        RawReceivedAt = T0,
        MappingVersion = 1,
        EventType = type,
        OccurredAt = T0,
        ReceivedAt = T0,
        Severity = severity,
        DeliveryKey = Guid.NewGuid().ToString("N"),
        IdentityConfidence = "exact",
        Fingerprint = "00ff",
        Summary = "s",
    };

    [Theory]
    [InlineData(EventTypes.Firing, Severity.Critical, false)]
    [InlineData(EventTypes.Firing, Severity.High, false)]
    [InlineData(EventTypes.Firing, Severity.Medium, false)]
    [InlineData(EventTypes.Firing, Severity.Unknown, false)]
    [InlineData(EventTypes.Firing, Severity.Low, true)]
    [InlineData(EventTypes.Firing, Severity.Informational, true)]
    [InlineData(EventTypes.Informational, Severity.High, true)]
    public void Queue_only_follows_the_severity_table_and_the_event_type(string type, Severity severity, bool queueOnly)
    {
        var episode = Episode.Open(Event(type, severity), "scope", T0, TimeProvider.System);
        episode.IsQueueOnly.Should().Be(queueOnly);
    }

    [Fact]
    public void A_low_episode_leaves_queue_only_when_its_severity_rises()
    {
        var episode = Episode.Open(Event(EventTypes.Firing, Severity.Low), "scope", T0, TimeProvider.System);
        episode.IsQueueOnly.Should().BeTrue();
        var transition = episode.ApplySignal(Event(EventTypes.Firing, Severity.Medium), T0.AddMinutes(1));
        transition.PreviousSeverity.Should().Be(Severity.Low);
        transition.IsMaterialSeverityIncrease.Should().BeFalse("medium is not a material increase (04 §6)");
        episode.IsQueueOnly.Should().BeFalse("but it is no longer queue only, so the hook announces it as a new actionable episode");
    }
}
