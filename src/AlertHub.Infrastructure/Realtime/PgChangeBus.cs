using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Realtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AlertHub.Infrastructure.Realtime;

/// <summary>Publishes change events with <c>pg_notify</c> so every API replica can fan them out to its SSE clients (ADR-12, no extra broker).</summary>
public sealed class PgChangeBroadcaster(NpgsqlDataSource dataSource, SseHub hub) : IChangeBroadcaster
{
    public const string Channel = "alerthub_changes";

    public async Task PublishAsync(ChangeEvent change, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new { type = change.Type, scope = change.Scope, data = change.DataJson }, JsonDefaults.Stored);
        // Same-process subscribers get it immediately; NOTIFY reaches the other replicas (and, echoed, this one — de-duplicated by the hub).
        hub.Publish(change, local: true);
        await using var cmd = dataSource.CreateCommand("SELECT pg_notify($1, $2)");
        cmd.Parameters.AddWithValue(Channel);
        cmd.Parameters.AddWithValue(payload.Length > 7900 ? payload[..7900] : payload);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Dedicated LISTEN connection; reconnects with backoff. Runs on the API host.</summary>
public sealed class PgChangeListener(NpgsqlDataSource dataSource, SseHub hub, ILogger<PgChangeListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await dataSource.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) =>
                {
                    try
                    {
                        var doc = JsonDocument.Parse(e.Payload);
                        var root = doc.RootElement;
                        hub.Publish(new ChangeEvent(root.GetProperty("type").GetString()!, root.GetProperty("scope").GetString()!, root.GetProperty("data").GetString()!), local: false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Ignoring malformed change notification");
                    }
                };
                await using (var cmd = new NpgsqlCommand($"LISTEN {PgChangeBroadcaster.Channel}", conn))
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                }
                logger.LogInformation("Listening for change notifications");
                delay = TimeSpan.FromSeconds(1);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await conn.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Change listener disconnected; retrying in {Delay}", delay);
                hub.MarkGap();
                try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }
    }
}

/// <summary>One stored event with its monotonic id.</summary>
public sealed record SseEvent(long Id, string Type, string Scope, string Data, DateTimeOffset At);

/// <summary>
/// In-process fan-out with a 5-minute ring buffer (ADR-12): clients reconnect with <c>Last-Event-ID</c>; events after that id
/// are replayed, otherwise the client is told to <c>resync</c>. Ids are monotonic per process; a replica switch or a listener
/// gap also forces a resync.
/// </summary>
public sealed class SseHub(TimeProvider time)
{
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, (Channel<SseEvent> Channel, IReadOnlySet<string>? Scopes)> _subscribers = new();
    private readonly LinkedList<SseEvent> _ring = [];
    private readonly Lock _lock = new();
    private readonly HashSet<string> _recentLocal = [];
    private long _nextId = 1;
    private long _gapMarker;

    /// <summary>Unique per process so a client that reconnects to another replica cannot present a foreign id as valid.</summary>
    public string Generation { get; } = Guid.NewGuid().ToString("N")[..8];

    public void Publish(ChangeEvent change, bool local)
    {
        var key = change.Type + "|" + change.Scope + "|" + change.DataJson;
        lock (_lock)
        {
            if (local)
            {
                _recentLocal.Add(key);
                if (_recentLocal.Count > 2000) _recentLocal.Clear();
            }
            else if (_recentLocal.Remove(key))
            {
                return; // echo of our own notify
            }
            var now = time.GetUtcNow();
            var evt = new SseEvent(_nextId++, change.Type, change.Scope, change.DataJson, now);
            _ring.AddLast(evt);
            while (_ring.First is { } first && now - first.Value.At > Retention) _ring.RemoveFirst();
            foreach (var (channel, scopes) in _subscribers.Values)
            {
                if (scopes is null || scopes.Contains(change.Scope)) channel.Writer.TryWrite(evt);
            }
        }
    }

    /// <summary>The listener lost its connection: ids before this point can no longer be trusted for replay.</summary>
    public void MarkGap()
    {
        lock (_lock) _gapMarker = _nextId;
    }

    /// <summary>Subscribes; returns the events to replay (or null for "resync") and the live channel.</summary>
    public (IReadOnlyList<SseEvent>? Replay, ChannelReader<SseEvent> Live, Guid Id) Subscribe(IReadOnlySet<string>? scopes, string? lastEventId)
    {
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        lock (_lock)
        {
            IReadOnlyList<SseEvent>? replay = [];
            if (lastEventId is not null)
            {
                replay = TryParse(lastEventId, out var since) && since >= _gapMarker && (_ring.First is null || since >= _ring.First.Value.Id - 1)
                    ? _ring.Where(e => e.Id > since && (scopes is null || scopes.Contains(e.Scope))).ToList()
                    : null;
            }
            _subscribers[id] = (channel, scopes);
            return (replay, channel.Reader, id);
        }
    }

    public void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var entry)) entry.Channel.Writer.TryComplete();
    }

    public string FormatId(long id) => $"{Generation}-{id}";

    private bool TryParse(string lastEventId, out long id)
    {
        id = 0;
        var dash = lastEventId.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0 || lastEventId[..dash] != Generation) return false;
        return long.TryParse(lastEventId.AsSpan(dash + 1), out id);
    }

    public int SubscriberCount => _subscribers.Count;
}
