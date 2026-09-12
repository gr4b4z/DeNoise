using System.Security.Cryptography;

namespace DeNoise.Application.Integrations;

/// <summary>
/// Ingest / heartbeat / PAT credentials per ADR-11: a 12-character non-secret key id for lookup and a
/// 32-byte base64url secret. Neither part is ever logged.
/// </summary>
public static class TokenGenerator
{
    public const int KeyIdLength = 12;
    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789"; // no 0/o/1/l to keep URLs unambiguous

    public static string NewKeyId()
    {
        var chars = new char[KeyIdLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)];
        }
        return new string(chars);
    }

    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
