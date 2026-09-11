using System.Text.Json;
using AlertHub.Application.Abstractions;
using AlertHub.Application.Audit;
using AlertHub.Application.Integrations;
using AlertHub.Domain.Audit;
using AlertHub.Domain.Common;
using AlertHub.Domain.Notifications;

namespace AlertHub.Application.Notifications;

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
    void Add(Destination destination);
}

public sealed record CreateDestination(
    string Name, string ChannelType, Guid? TeamId, Guid? FallbackDestinationId,
    string? Url = null, string Method = "POST", IReadOnlyDictionary<string, string>? Headers = null, string? SigningSecret = null,
    TimeSpan? Timeout = null, string[]? EventTypes = null, string[]? EmailTo = null, Guid? OwnerUserId = null);

/// <summary>What the API returns once on creation: the generated signing secret, never stored in plaintext.</summary>
public sealed record DestinationCreated(Destination Destination, string? SigningSecret);

public sealed class NotificationOptions
{
    public const string Section = "Notifications";
    /// <summary>Allow <c>http://</c> destination URLs (development only; ADR-7).</summary>
    public bool AllowInsecureDestinations { get; set; }
    /// <summary>Base URL of the UI, used for links in notifications (<c>publicBaseUrl</c>).</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5173";
    public int MaxAttempts { get; set; } = 10;
    public string UserAgent { get; set; } = "AlertHub/" + (typeof(NotificationOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");
}

public sealed class DestinationService(IDestinationRepository destinations, ISecretProtector protector, IAuditWriter audit, IUnitOfWork uow, TimeProvider time, Microsoft.Extensions.Options.IOptions<NotificationOptions> options)
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
