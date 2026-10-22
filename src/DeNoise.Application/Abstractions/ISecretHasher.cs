namespace DeNoise.Application.Abstractions;

/// <summary>
/// Argon2id hashing for tokens and passwords (ADR-11, ADR-14). Implementations own the parameters;
/// the encoded hash carries them so parameters can be raised without invalidating stored hashes.
/// </summary>
public interface ISecretHasher
{
    /// <summary>Hash a high-entropy token (ingest, heartbeat, personal access token).</summary>
    string HashToken(string token);

    /// <summary>Hash a human password with the ADR-14 parameters (m=64 MiB, t=3, p=1).</summary>
    string HashPassword(string password);

    /// <summary>Constant-time verification against an encoded hash; false for malformed hashes.</summary>
    bool Verify(string secret, string encodedHash);
}
