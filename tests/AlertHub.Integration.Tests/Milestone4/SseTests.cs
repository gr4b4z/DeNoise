using System.Net;
using System.Net.Http.Headers;
using AlertHub.Contracts;
using AlertHub.Infrastructure.Realtime;
using AlertHub.Integration.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace AlertHub.Integration.Tests.Milestone4;

/// <summary>ADR-12: scope-filtered stream, ids, <c>Last-Event-ID</c> replay, resync on unknown ids — the server half of <i>UI loses its live connection</i>.</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SseTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "sse");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    private async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenStreamAsync(ApiClient client, string? lastEventId = null)
    {
        var http = _api.Factory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/events/stream", UriKind.Relative));
        request.Headers.Add("Cookie", $"alerthub_session={client.Cookie}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (lastEventId is not null) request.Headers.Add("Last-Event-ID", lastEventId);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        return (response, new StreamReader(await response.Content.ReadAsStreamAsync()));
    }

    private static async Task<List<string>> ReadBlockAsync(StreamReader reader, TimeSpan timeout)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null) break;
            if (line.Length == 0)
            {
                if (lines.Count > 0) break;
                continue;
            }
            lines.Add(line);
        }
        return lines;
    }

    [Fact]
    public async Task Episode_changes_reach_subscribers_in_scope_with_ids_and_replay_after_reconnect()
    {
        using var op = await _api.LoginAsync("operator");
        var (response, reader) = await OpenStreamAsync(op);
        using var _ = response;
        (await ReadBlockAsync(reader, TimeSpan.FromSeconds(5))).Should().Contain(l => l.StartsWith(": connected", StringComparison.Ordinal));

        var inScope = await _api.OpenEpisodeAsync("sse-1");
        var block = await ReadBlockAsync(reader, TimeSpan.FromSeconds(10));
        block.Should().Contain("event: episode.changed");
        block.Should().Contain(l => l.StartsWith("id: ", StringComparison.Ordinal));
        block.Single(l => l.StartsWith("data: ", StringComparison.Ordinal)).Should().Contain(inScope.ToString()).And.Contain("\"handling\":\"new\"");
        var lastId = block.Single(l => l.StartsWith("id: ", StringComparison.Ordinal))["id: ".Length..];

        // Out-of-scope change: never delivered to this subscriber.
        await _api.OpenEpisodeAsync("sse-b", _api.IntegrationB);
        // An action through the API also publishes.
        var detail = await op.GetAsync<EpisodeDetail>($"/api/v1/episodes/{inScope}");
        var (status, _, _) = await op.PostAsync<EpisodeDetail>($"/api/v1/episodes/{inScope}/ack", new AckRequest(), ifMatch: detail!.Item.Version);
        status.Should().Be(HttpStatusCode.OK);
        var ackBlock = await ReadBlockAsync(reader, TimeSpan.FromSeconds(10));
        ackBlock.Single(l => l.StartsWith("data: ", StringComparison.Ordinal)).Should().Contain("\"handling\":\"acknowledged\"").And.Contain(inScope.ToString());

        // Client disconnects (e.g. laptop lid) and something happens meanwhile.
        response.Dispose();
        reader.Dispose();
        var missed = await _api.OpenEpisodeAsync("sse-2");

        // Reconnect with Last-Event-ID: the missed events are replayed, no resync.
        var (response2, reader2) = await OpenStreamAsync(op, lastId);
        using var __ = response2;
        var connected = await ReadBlockAsync(reader2, TimeSpan.FromSeconds(5));
        connected.Should().NotContain("event: resync");
        var replayed = new List<string>();
        for (var i = 0; i < 2; i++) replayed.AddRange(await ReadBlockAsync(reader2, TimeSpan.FromSeconds(5)));
        replayed.Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Should().Contain(l => l.Contains(missed.ToString(), StringComparison.Ordinal))
            .And.Contain(l => l.Contains("\"handling\":\"acknowledged\"", StringComparison.Ordinal), "the ack that happened while connected is after lastId too");
        replayed.Should().NotContain(l => l.Contains("sse-b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unknown_or_foreign_last_event_id_triggers_resync()
    {
        using var op = await _api.LoginAsync("operator");
        var (response, reader) = await OpenStreamAsync(op, "otherproc-42");
        using var _ = response;
        var first = await ReadBlockAsync(reader, TimeSpan.FromSeconds(5));
        var second = first.Contains("event: resync") ? first : await ReadBlockAsync(reader, TimeSpan.FromSeconds(5));
        second.Should().Contain("event: resync");
    }

    [Fact]
    public void Hub_ring_buffer_replays_within_retention_and_drops_older_events()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(ApiFixture.T0);
        var hub = new SseHub(time);
        var scopes = new HashSet<string> { "s" };
        hub.Publish(new Application.Realtime.ChangeEvent("episode.changed", "s", "{\"n\":1}"), local: true);
        time.Advance(TimeSpan.FromMinutes(6));
        hub.Publish(new Application.Realtime.ChangeEvent("episode.changed", "s", "{\"n\":2}"), local: true);
        hub.Publish(new Application.Realtime.ChangeEvent("episode.changed", "other", "{\"n\":3}"), local: true);

        var (replay, _, id) = hub.Subscribe(scopes, hub.FormatId(1));
        replay.Should().NotBeNull();
        replay!.Select(e => e.Data).Should().Equal(["{\"n\":2}"], "the first event aged out, the other-scope event is filtered");
        hub.Unsubscribe(id);

        var (gap, _, id2) = hub.Subscribe(scopes, hub.FormatId(0));
        gap.Should().BeNull("id 0 predates the ring");
        hub.Unsubscribe(id2);
    }

    [Fact]
    public async Task Unauthenticated_stream_is_401()
    {
        using var http = _api.Factory.CreateClient();
        using var response = await http.GetAsync(new Uri("/api/v1/events/stream", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = _api.Factory.Services.GetRequiredService<SseHub>().SubscriberCount;
    }
}
