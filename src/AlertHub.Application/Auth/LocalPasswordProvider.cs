using AlertHub.Application.Abstractions;
using AlertHub.Domain.Common;
using AlertHub.Domain.Users;
using Microsoft.Extensions.Options;

namespace AlertHub.Application.Auth;

/// <summary>
/// Username + password with Argon2id (ADR-14). Lockout: 10 failures / 15 min per user <b>and</b> per IP, counted from
/// <c>cfg.login_attempt</c>. Unknown user and wrong password take the same path (a dummy hash is verified) so timing and
/// the response are indistinguishable; a disabled user is treated as invalid credentials.
/// </summary>
public sealed class LocalPasswordProvider(
    IUserRepository users, ILoginAttemptRepository attempts, ISecretHasher hasher, IUnitOfWork uow, TimeProvider time, IOptions<LocalAuthOptions> options) : IIdentityProvider
{
    public string Name => AuthProviders.Local;

    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, System.Net.IPAddress? ip, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var normalised = username.Trim();
        var since = now - options.Value.LockoutWindow;

        var user = await users.FindByUsernameAsync(normalised, ct);
        var lockedUser = user is not null && user.IsLocked(now);
        var ipFailures = ip is null ? 0 : await attempts.CountFailuresAsync(null, ip, since, ct);
        if (lockedUser || ipFailures >= options.Value.LockoutFailures)
        {
            var until = lockedUser ? user!.LockedUntil : now + options.Value.LockoutWindow;
            attempts.Add(new LoginAttempt { Id = Ids.New(time), At = now, Username = normalised, Ip = ip, Success = false });
            await uow.CommitAsync(ct);
            return new AuthenticationResult(AuthenticationStatus.Locked, LockedUntil: until);
        }

        var hash = user?.PasswordHash ?? DummyHashValue(hasher);
        var ok = hasher.Verify(password, hash) && user is not null && !user.Disabled && user.AuthProvider == AuthProviders.Local && user.PasswordHash is not null;

        attempts.Add(new LoginAttempt { Id = Ids.New(time), At = now, Username = normalised, Ip = ip, Success = ok });
        if (!ok)
        {
            if (user is not null && !user.Disabled)
            {
                user.FailedAttempts++;
                user.UpdatedAt = now;
                if (user.FailedAttempts >= options.Value.LockoutFailures)
                {
                    // The tenth failure is still answered as invalid credentials; from the eleventh attempt on the account is locked.
                    user.LockedUntil = now + options.Value.LockoutWindow;
                    user.FailedAttempts = 0;
                }
            }
            await uow.CommitAsync(ct);
            return new AuthenticationResult(AuthenticationStatus.InvalidCredentials);
        }

        user!.FailedAttempts = 0;
        user.LockedUntil = null;
        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await uow.CommitAsync(ct);
        return new AuthenticationResult(AuthenticationStatus.Success, user);
    }

    // A real Argon2id hash of a random value: verifying an unknown user against it costs the same as a genuine check,
    // so the response time does not reveal whether the username exists.
    private static string? _dummy;
    private static string DummyHashValue(ISecretHasher hasher) => _dummy ??= hasher.HashPassword(Guid.NewGuid().ToString("N") + "-dummy-" + Guid.NewGuid().ToString("N"));
}
