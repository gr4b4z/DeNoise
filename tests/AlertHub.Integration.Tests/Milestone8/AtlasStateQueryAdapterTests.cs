using System.Net;
using AlertHub.Application.Notifications;
using AlertHub.Application.Processing;
using AlertHub.Domain.Alerts;
using AlertHub.Domain.Common;
using AlertHub.Domain.Episodes;
using AlertHub.Domain.Integrations;
using AlertHub.Infrastructure.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlertHub.Integration.Tests.Milestone8;

/// <summary>Atlas Admin API adapter against a scripted HTTP handler: every response class maps to one explicit outcome, nothing is guessed.</summary>
[Trait("Category", "Unit")]
public sealed class AtlasStateQueryAdapterTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext["enc:".Length..];
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(ScriptedHandler handler) : IAtlasHttpHandlerFactory
    {
        public HttpMessageHandler Create(AtlasApiSettings settings) => handler;
    }

    private static readonly PlainProtector Protector = new();

    private static Domain.Integrations.Integration Atlas(bool withCredentials = true) => new()
    {
        IntegrationId = Guid.NewGuid(),
        Name = "atlas",
        Type = IntegrationTypes.Atlas,
        AccessScope = "s",
        IngestKeyId = "k",
        IngestTokenHash = "h",
        Capabilities = withCredentials ? AtlasCapabilities.Configure("{}", "5f1234567890abcdef123456", "pub-key", "priv-key", null, Protector) : "{}",
    };

    private static Episode EpisodeFor(string alertId)
    {
        var evt = new NormalisedEvent { EventId = Guid.NewGuid(), IntegrationId = Guid.NewGuid(), RawReceivedAt = DateTimeOffset.UtcNow, MappingVersion = 1, EventType = EventTypes.Firing, SourceAlertId = alertId, ReceivedAt = DateTimeOffset.UtcNow, Severity = Severity.High, DeliveryKey = "dk", IdentityConfidence = "exact", Fingerprint = "fp", RuleId = "r" };
        return Episode.Open(evt, "s", DateTimeOffset.UtcNow, TimeProvider.System);
    }

    private static (AtlasStateQueryAdapter Adapter, ScriptedHandler Handler) Build(HttpStatusCode status, string body = "")
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        return (new AtlasStateQueryAdapter(Protector, NullLogger<AtlasStateQueryAdapter>.Instance, new Factory(handler)), handler);
    }

    [Fact]
    public void Capabilities_store_encrypted_keys_and_describe_without_them()
    {
        var integration = Atlas();
        integration.Capabilities.Should().Contain("\"state_query\":true").And.Contain("credentials_enc").And.NotContain("private_key");
        var settings = AtlasCapabilities.Read(integration, Protector)!;
        settings.GroupId.Should().Be("5f1234567890abcdef123456");
        settings.PublicKey.Should().Be("pub-key");
        settings.PrivateKey.Should().Be("priv-key");
        settings.BaseUrl.Should().Be(new Uri(AtlasCapabilities.DefaultBaseUrl));
        AtlasCapabilities.Describe(integration.Capabilities).Should().Be(("5f1234567890abcdef123456", AtlasCapabilities.DefaultBaseUrl, true));
        AtlasCapabilities.Read(Atlas(withCredentials: false), Protector).Should().BeNull();
    }

    [Theory]
    [InlineData("OPEN", StateQueryOutcome.Active)]
    [InlineData("TRACKING", StateQueryOutcome.Active)]
    [InlineData("CLOSED", StateQueryOutcome.NotActive)]
    [InlineData("CANCELLED", StateQueryOutcome.NotActive)]
    [InlineData("WEIRD", StateQueryOutcome.Error)]
    public async Task Alert_status_maps_to_the_state_query_outcome(string status, StateQueryOutcome expected)
    {
        var (adapter, handler) = Build(HttpStatusCode.OK, $$"""{"id":"alert-1","status":"{{status}}"}""");
        using var _ = adapter;
        var result = await adapter.QueryAsync(Atlas(), EpisodeFor("alert-1"));
        result.Outcome.Should().Be(expected);
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsoluteUri.Should().Be("https://cloud.mongodb.com/api/atlas/v2/groups/5f1234567890abcdef123456/alerts/alert-1");
        handler.Requests[0].Headers.Accept.Should().Contain(a => a.MediaType == AtlasCapabilities.ApiVersionMediaType);
    }

    [Fact]
    public async Task Http_failures_are_errors_or_not_active_never_guesses()
    {
        using var notFound = Build(HttpStatusCode.NotFound).Adapter;
        (await notFound.QueryAsync(Atlas(), EpisodeFor("gone"))).Outcome.Should().Be(StateQueryOutcome.NotActive);
        using var unauthorised = Build(HttpStatusCode.Unauthorized).Adapter;
        (await unauthorised.QueryAsync(Atlas(), EpisodeFor("a"))).Outcome.Should().Be(StateQueryOutcome.Error);
        using var unsupported = Build(HttpStatusCode.OK).Adapter;
        (await unsupported.QueryAsync(Atlas(withCredentials: false), EpisodeFor("a"))).Outcome.Should().Be(StateQueryOutcome.Unsupported);
        (await unsupported.QueryAsync(Atlas(), EpisodeFor(null!))).Outcome.Should().Be(StateQueryOutcome.Error, "no source alert id to ask about");
    }

    [Fact]
    public async Task Probe_reports_reachability_and_the_router_dispatches_by_type()
    {
        var (adapter, handler) = Build(HttpStatusCode.OK, """{"id":"5f1234567890abcdef123456"}""");
        using var _ = adapter;
        var router = new StateQueryAdapterRouter(adapter);
        var ok = await router.ProbeAsync(Atlas());
        ok.Should().Be(new ProbeResult(true, true, "atlas project reachable (200)"));
        handler.Requests.Should().ContainSingle().Which.RequestUri!.AbsolutePath.Should().Be("/api/atlas/v2/groups/5f1234567890abcdef123456");

        var azure = new Domain.Integrations.Integration { IntegrationId = Guid.NewGuid(), Name = "az", Type = IntegrationTypes.AzureMonitor, AccessScope = "s", IngestKeyId = "k", IngestTokenHash = "h" };
        (await router.ProbeAsync(azure)).Supported.Should().BeFalse();
        (await router.QueryAsync(azure, EpisodeFor("x"))).Outcome.Should().Be(StateQueryOutcome.Unsupported);

        using var failing = Build(HttpStatusCode.Forbidden).Adapter;
        (await failing.ProbeAsync(Atlas())).Should().Be(new ProbeResult(true, false, "atlas api responded 403"));
    }
}
