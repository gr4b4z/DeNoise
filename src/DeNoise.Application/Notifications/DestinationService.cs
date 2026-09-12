using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Application.Audit;
using DeNoise.Application.Integrations;
using DeNoise.Domain.Audit;
using DeNoise.Domain.Common;
using DeNoise.Domain.Notifications;

namespace DeNoise.Application.Notifications;

/// <summary>Encrypts per-row secrets (destination URLs, headers, signing secrets) with the platform data-protection key (ADR-11).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

public interface IDestinationRepository
{
    Task<Destination?> GetAsync(Guid destinationId, CancellationToken ct = default);
    Task<IReadOnlyList<Destination>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Destination>> ListForTeamAsync(Guid teamId, CancellationToken ct = default);
    /// <summary>Tracked load for an update in the ambient unit of work.</summary>
    Task<Destination?> GetTrackedAsync(Guid destinationId, CancellationToken ct = default);
    void Add(Destination destination);
}

public sealed record CreateDestination(
    string Name, string ChannelType, Guid? TeamId, Guid? FallbackDestinationId,
    string? Url = null, string Method = "POST", IReadOnlyDictionary<string, string>? Headers = null, string? SigningSecret = null,
    TimeSpan? Timeout = null, string[]? EventTypes = null, string[]? EmailTo = null, Guid? OwnerUserId = null);

/// <summary>What the API returns once on creation: the generated signing secret, never stored in plaintext.</summary>
public sealed record DestinationCreated(Destination Destination, string? SigningSecret);

/// <summary>Fields of an update (06 §4 <c>PUT /destinations/{id}</c>); null leaves a field unchanged, secrets are replaced only when supplied.</summary>
public sealed record UpdateDestination(
    string? Name = null, Guid? TeamId = null, Guid? FallbackDestinationId = null, string? Url = null, string? Method = null, IReadOnlyDictionary<string, string>? Headers = null,
    bool RotateSigningSecret = false, TimeSpan? Timeout = null, string[]? EventTypes = null, string[]? EmailTo = null, Guid? BodyTemplateId = null, bool ClearBodyTemplate = false, bool? Active = null);

/// <summary>Outcome of <c>POST /destinations/{id}/test</c>: a synthetic <c>episode.opened</c> sent to the real URL (06 §7).</summary>
public sealed record TestSendResult(string Outcome, int? HttpStatus, int LatencyMs, string? Error, string? ResponseExcerpt, string RenderedBody, string ContentType);

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    /// <summary>Allow <c>http://</c> destination URLs (development only; ADR-7).</summary>
    public bool AllowInsecureDestinations { get; set; }
    /// <summary>Base URL of the UI, used for links in notifications (<c>publicBaseUrl</c>).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5173";
    public int MaxAttempts { get; set; } = 10;
    public string UserAgent { get; set; } = "DeNoise/" + (typeof(NotificationOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
}

public sealed class DestinationService(IDestinationRepository destinations, ISecretProtector protector, IAuditWriter audit, IUnitOfWork uow, TimeProvider time, Microsoft.Extensions.Options.IOptions<NotificationOptions> options,
    Templates.TemplateService templates, IEnumerable<INotificationChannel> channels, Templates.ITemplateRepository templateRepository, IOutboxQueue outbox)
{
    /// <summary>
    /// Creates a destination. When <paramref name="request"/> has no fallback, the destination becomes its own bootstrap
    /// pair: the ★ constraint requires a fallback ≠ self, so a first destination must be created together with a partner
    /// via <see cref="CreatePairAsync"/>; otherwise the fallback must exist.
    /// </summary>
    public async Task<DestinationCreated> CreateAsync(CreateDestination request, Actor actor, CancellationToken ct = default)
    {
        var fallbackId = request.FallbackDestinationId ?? throw new ArgumentException("fallback destination is required (ADR-7)", nameof(request));
        if (await destinations.GetAsync(fallbackId, ct) is null) throw new KeyNotFoundException($"Fallback destination {fallbackId} not found.");
        var (destination, secret) = Build(request, fallbackId, actor);
        destinations.Add(destination);
        Audit(destination, actor);
        await uow.CommitAsync(ct);
        return new DestinationCreated(destination, secret);
    }

    /// <summary>Creates two destinations that fall back to each other in one transaction (bootstrap for a team).</summary>
    public async Task<(DestinationCreated Primary, DestinationCreated Fallback)> CreatePairAsync(CreateDestination primary, CreateDestination fallback, Actor actor, CancellationToken ct = default)
    {
        var primaryId = Ids.New(time);
        var fallbackId = Ids.New(time);
        var (p, ps) = Build(primary with { FallbackDestinationId = fallbackId }, fallbackId, actor, primaryId);
        var (f, fs) = Build(fallback with { FallbackDestinationId = primaryId }, primaryId, actor, fallbackId);
        destinations.Add(p);
        destinations.Add(f);
        Audit(p, actor);
        Audit(f, actor);
        await uow.CommitAsync(ct);
        return (new DestinationCreated(p, ps), new DestinationCreated(f, fs));
    }

    public string? RevealUrl(Destination destination) => destination.UrlEnc is null ? null : protector.Unprotect(destination.UrlEnc);

    public async Task<Destination> UpdateAsync(Guid id, int expectedVersion, UpdateDestination request, Actor actor, CancellationToken ct = default)
    {
        var destination = await destinations.GetTrackedAsync(id, ct) ?? throw new KeyNotFoundException($"Destination {id} not found.");
        if (destination.Version != expectedVersion) throw new Episodes.VersionConflictException(id, destination.Version);
        var before = Snapshot(destination);
        if (request.Name is { Length: > 0 } name) destination.Name = name.Trim();
        if (request.TeamId is { } team) destination.TeamId = team;
        if (request.FallbackDestinationId is { } fallback)
        {
            if (fallback == id) throw new ArgumentException("a destination cannot be its own fallback (ADR-7)", nameof(request));
            if (await destinations.GetAsync(fallback, ct) is null) throw new KeyNotFoundException($"Fallback destination {fallback} not found.");
            destination.FallbackDestinationId = fallback;
        }
        if (request.EventTypes is { } types)
        {
            var unknown = types.Where(t => !NotificationTypes.All.Contains(t)).ToList();
            if (unknown.Count > 0) throw new ArgumentException($"Unknown event types: {string.Join(", ", unknown)}", nameof(request));
            destination.EventTypes = types;
        }
        if (request.Timeout is { } timeout)
        {
            if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromSeconds(60)) throw new ArgumentException("timeout must be between 1 and 60 seconds", nameof(request));
            destination.Timeout = timeout;
        }
        if (request.Active is { } active) destination.Active = active;
        string? secret = null;
        if (destination.ChannelType == ChannelTypes.Webhook)
        {
            if (request.Url is { Length: > 0 } url)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ArgumentException("webhook destinations need an absolute URL", nameof(request));
                if (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowInsecureDestinations && uri.Scheme == Uri.UriSchemeHttp)) throw new ArgumentException("destination URLs must be https", nameof(request));
                destination.UrlEnc = protector.Protect(uri.ToString());
            }
            if (request.Method is { Length: > 0 } method) destination.Method = method.ToUpperInvariant();
            if (request.Headers is not null) destination.HeadersEnc = request.Headers.Count == 0 ? null : protector.Protect(JsonSerializer.Serialize(request.Headers, JsonDefaults.Stored));
            if (request.RotateSigningSecret)
            {
                secret = TokenGenerator.NewSecret();
                destination.SigningSecretEnc = protector.Protect(secret);
            }
            if (request.ClearBodyTemplate) destination.BodyTemplateId = null;
            else if (request.BodyTemplateId is { } templateId)
            {
                if (await templateRepository.GetActiveAsync(templateId, ct) is null) throw new KeyNotFoundException($"Template {templateId} has no active version.");
                destination.BodyTemplateId = templateId;
            }
        }
        else if (request.EmailTo is { Length: > 0 } to)
        {
            destination.EmailTo = to;
        }
        destination.Version++;
        destination.UpdatedAt = time.GetUtcNow();
        audit.Record(Entry(actor, "destination.update", destination, before, Snapshot(destination)));
        await uow.CommitAsync(ct);
        _lastRotatedSecret = secret;
        return destination;
    }

    private string? _lastRotatedSecret;
    /// <summary>The signing secret generated by the last <see cref="UpdateAsync"/> with <c>RotateSigningSecret</c>, returned once.</summary>
    public string? TakeRotatedSecret()
    {
        var s = _lastRotatedSecret;
        _lastRotatedSecret = null;
        return s;
    }

    /// <summary>Sends a synthetic <c>episode.opened</c> through the real channel to the real URL; nothing is queued or retried (06 §7).</summary>
    public async Task<TestSendResult> TestSendAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        var destination = await destinations.GetAsync(id, ct) ?? throw new KeyNotFoundException($"Destination {id} not found.");
        var channel = channels.FirstOrDefault(c => c.ChannelType == destination.ChannelType) ?? throw new NotSupportedException($"no channel for '{destination.ChannelType}'");
        var now = time.GetUtcNow();
        var message = new Domain.Ops.OutboxMessage { OutboxId = Ids.New(time), Type = NotificationTypes.DestinationTest, DestinationId = id, Payload = "{}", NotBefore = now, CreatedAt = now };
        var model = NotificationModel.Sample(now, NotificationTypes.DestinationTest);
        model["deliveryId"] = message.OutboxId.ToString();
        model["test"] = true;
        string body;
        var contentType = "application/json";
        if (destination.ChannelType == ChannelTypes.Webhook && destination.BodyTemplateId is not null)
        {
            try
            {
                var rendered = await templates.RenderForDestinationAsync(destination, model, ct);
                body = rendered.Body;
                contentType = rendered.ContentType;
            }
            catch (Templates.TemplateRenderException ex)
            {
                var error = $"template render failed: {ex.Message}";
                await RecordTestAsync(message, destination, ChannelResult.Permanent(error), 0, now, ct);
                audit.Record(Entry(actor, "destination.test", destination, null, new { Outcome = DeliveryOutcomes.Permanent, error }));
                await uow.CommitAsync(ct);
                return new TestSendResult(DeliveryOutcomes.Permanent, null, 0, error, null, string.Empty, contentType);
            }
        }
        else
        {
            body = model.ToJsonString(JsonDefaults.Stored);
        }
        var resolved = new ResolvedDestination(destination, RevealUrl(destination), Headers(destination), destination.SigningSecretEnc is null ? null : protector.Unprotect(destination.SigningSecretEnc), contentType);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await channel.SendAsync(resolved, message, body, ct);
        var latency = (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await RecordTestAsync(message, destination, result, latency, now, ct);
        audit.Record(Entry(actor, "destination.test", destination, null, new { result.Outcome, result.HttpStatus, latency }));
        await uow.CommitAsync(ct);
        return new TestSendResult(result.Outcome, result.HttpStatus, latency, result.Error, result.ResponseExcerpt, body, contentType);
    }

    /// <summary>
    /// A test send is never retried, so its outbox row is written already terminal (<c>sent</c> / <c>failed</c>) together with
    /// its delivery attempt: it shows up in the destination's deliveries and moves the health counters like a real delivery.
    /// </summary>
    private async Task RecordTestAsync(Domain.Ops.OutboxMessage message, Destination destination, ChannelResult result, int latency, DateTimeOffset now, CancellationToken ct)
    {
        var success = result.Outcome == DeliveryOutcomes.Success;
        message.Status = success ? Domain.Ops.OutboxStatus.Sent : Domain.Ops.OutboxStatus.Failed;
        message.Attempts = 1;
        message.SentAt = success ? now : null;
        message.LastError = result.Error;
        await outbox.EnqueueAsync(message, ct);
        await outbox.RecordAttemptAsync(new DeliveryAttempt
        {
            Id = Ids.New(time),
            OutboxId = message.OutboxId,
            AttemptedAt = now,
            Channel = destination.ChannelType,
            Outcome = result.Outcome,
            HttpStatus = result.HttpStatus,
            LatencyMs = latency,
            Error = result.Error,
            UsedFallback = false,
            ResponseExcerpt = result.ResponseExcerpt,
        }, ct);
        await outbox.UpdateDestinationHealthAsync(destination.DestinationId, success, now, ct);
    }

    public Dictionary<string, string> RevealHeaders(Destination destination) => new(Headers(destination));

    private IReadOnlyDictionary<string, string> Headers(Destination destination)
    {
        if (destination.HeadersEnc is null) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(protector.Unprotect(destination.HeadersEnc), JsonDefaults.Stored) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static object Snapshot(Destination d) => new { d.Name, d.TeamId, d.Method, d.Timeout, d.EventTypes, d.FallbackDestinationId, d.BodyTemplateId, d.Active, hasUrl = d.UrlEnc is not null, hasHeaders = d.HeadersEnc is not null };

    private AuditEntry Entry(Actor actor, string action, Destination destination, object? before, object? after)
    {
        var now = time.GetUtcNow();
        return new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = action,
            TargetType = "destination",
            TargetId = destination.DestinationId.ToString(),
            Before = before is null ? null : JsonSerializer.Serialize(before, JsonDefaults.Stored),
            After = after is null ? null : JsonSerializer.Serialize(after, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        };
    }

    private (Destination, string?) Build(CreateDestination request, Guid fallbackId, Actor actor, Guid? id = null)
    {
        if (!ChannelTypes.All.Contains(request.ChannelType)) throw new ArgumentException($"Unknown channel type '{request.ChannelType}'.", nameof(request));
        var unknownTypes = (request.EventTypes ?? []).Where(t => !NotificationTypes.All.Contains(t)).ToList();
        if (unknownTypes.Count > 0) throw new ArgumentException($"Unknown event types: {string.Join(", ", unknownTypes)}", nameof(request));

        var now = time.GetUtcNow();
        var destination = new Destination
        {
            DestinationId = id ?? Ids.New(time),
            TeamId = request.TeamId,
            Name = request.Name.Trim(),
            ChannelType = request.ChannelType,
            Method = request.Method.ToUpperInvariant(),
            Timeout = request.Timeout ?? TimeSpan.FromSeconds(10),
            EventTypes = request.EventTypes ?? NotificationTypes.DefaultSubscription,
            FallbackDestinationId = fallbackId,
            OwnerUserId = request.OwnerUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        string? secret = null;
        switch (request.ChannelType)
        {
            case ChannelTypes.Webhook:
                if (string.IsNullOrWhiteSpace(request.Url) || !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
                {
                    throw new ArgumentException("webhook destinations need an absolute URL", nameof(request));
                }
                if (uri.Scheme != Uri.UriSchemeHttps && !(options.Value.AllowInsecureDestinations && uri.Scheme == Uri.UriSchemeHttp))
                {
                    throw new ArgumentException("destination URLs must be https (set Notifications:AllowInsecureDestinations for development)", nameof(request));
                }
                destination.UrlEnc = protector.Protect(uri.ToString());
                if (request.Headers is { Count: > 0 }) destination.HeadersEnc = protector.Protect(JsonSerializer.Serialize(request.Headers, JsonDefaults.Stored));
                secret = request.SigningSecret ?? TokenGenerator.NewSecret();
                destination.SigningSecretEnc = protector.Protect(secret);
                break;
            case ChannelTypes.SmtpEmail:
                if (request.EmailTo is not { Length: > 0 }) throw new ArgumentException("email destinations need at least one recipient", nameof(request));
                destination.EmailTo = request.EmailTo;
                break;
        }
        _ = actor;
        return (destination, secret);
    }

    private void Audit(Destination destination, Actor actor)
    {
        var now = time.GetUtcNow();
        audit.Record(new AuditEntry
        {
            Id = Ids.New(time),
            At = now,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorDisplay = actor.Display,
            Action = "destination.create",
            TargetType = "destination",
            TargetId = destination.DestinationId.ToString(),
            After = JsonSerializer.Serialize(new { destination.Name, destination.ChannelType, destination.TeamId, destination.EventTypes, destination.FallbackDestinationId }, JsonDefaults.Stored),
            CorrelationId = actor.CorrelationId,
            RequestIp = actor.Ip,
        });
    }
}
