using DeNoise.Application.Integrations;
using DeNoise.Domain.Users;
using Microsoft.Extensions.Options;

namespace DeNoise.Application.Auth;

/// <summary>A session staged in the unit of work; <see cref="Token"/> is the plaintext that goes into the cookie and nowhere else.</summary>
public sealed record IssuedSession(string Token, Session Session);

/// <summary>
/// Creates the server-side session (ADR-14) for any identity provider: 32 random bytes in the <c>denoise_session</c> cookie,
/// only their SHA-256 in <c>cfg.session</c>, sliding/absolute lifetimes from <see cref="LocalAuthOptions"/>. Stages the row;
/// the caller commits it together with its own audit entries.
/// </summary>
public sealed class SessionIssuer(ISessionRepository sessions, TimeProvider time, IOptions<LocalAuthOptions> options)
{
    public IssuedSession Issue(User user, System.Net.IPAddress? ip, string? userAgent)
    {
        var now = time.GetUtcNow();
        var token = TokenGenerator.NewSecret();
        var session = new Session
        {
            SessionId = AuthService.HashToken(token),
            UserId = user.UserId,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + options.Value.SessionSliding,
            AbsoluteExpiresAt = now + options.Value.SessionAbsolute,
            Ip = ip,
            UserAgent = userAgent is null ? null : userAgent.Length <= 300 ? userAgent : userAgent[..300],
        };
        sessions.Add(session);
        return new IssuedSession(token, session);
    }
}
