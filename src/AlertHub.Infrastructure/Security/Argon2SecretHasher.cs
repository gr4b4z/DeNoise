using System.Security.Cryptography;
using System.Text;
using AlertHub.Application.Abstractions;
using Konscious.Security.Cryptography;

namespace AlertHub.Infrastructure.Security;

/// <summary>
/// Argon2id with a PHC-style encoding <c>$argon2id$v=19$m=…,t=…,p=…$salt$hash</c>. Passwords use the ADR-14
/// parameters; high-entropy tokens use lighter parameters because brute force against 256 random bits is not
/// the threat and ingestion must stay under its latency target.
/// </summary>
public sealed class Argon2SecretHasher : ISecretHasher
{
    public sealed record Parameters(int MemoryKiB, int Iterations, int Parallelism);

    public static readonly Parameters PasswordParameters = new(64 * 1024, 3, 1);
    public static readonly Parameters TokenParameters = new(16 * 1024, 2, 1);
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string HashToken(string token) => Hash(token, TokenParameters);
    public string HashPassword(string password) => Hash(password, PasswordParameters);

    public bool Verify(string secret, string encodedHash)
    {
        if (!TryParse(encodedHash, out var p, out var salt, out var expected)) return false;
        var actual = Derive(secret, salt, p, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Hash(string secret, Parameters p)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(secret, salt, p, HashBytes);
        return $"$argon2id$v=19$m={p.MemoryKiB},t={p.Iterations},p={p.Parallelism}${Convert.ToBase64String(salt).TrimEnd('=')}${Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    private static byte[] Derive(string secret, byte[] salt, Parameters p, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(secret))
        {
            Salt = salt,
            MemorySize = p.MemoryKiB,
            Iterations = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(length);
    }

    internal static bool TryParse(string encoded, out Parameters parameters, out byte[] salt, out byte[] hash)
    {
        parameters = default!;
        salt = hash = [];
        var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || parts[0] != "argon2id" || parts[1] != "v=19") return false;
        int m = 0, t = 0, par = 0;
        foreach (var kv in parts[2].Split(','))
        {
            var eq = kv.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0 || !int.TryParse(kv.AsSpan(eq + 1), out var value)) return false;
            switch (kv[..eq])
            {
                case "m": m = value; break;
                case "t": t = value; break;
                case "p": par = value; break;
                default: return false;
            }
        }
        if (m <= 0 || t <= 0 || par <= 0) return false;
        try
        {
            salt = Convert.FromBase64String(Pad(parts[3]));
            hash = Convert.FromBase64String(Pad(parts[4]));
        }
        catch (FormatException)
        {
            return false;
        }
        parameters = new Parameters(m, t, par);
        return true;
    }

    private static string Pad(string b64) => b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
}
