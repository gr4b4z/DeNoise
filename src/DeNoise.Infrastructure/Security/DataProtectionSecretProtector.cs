using DeNoise.Application.Notifications;
using Microsoft.AspNetCore.DataProtection;

namespace DeNoise.Infrastructure.Security;

/// <summary>
/// Per-row secret encryption via ASP.NET Data Protection (ADR-11). The key ring location comes from
/// <c>DataProtection:KeysPath</c> (a mounted volume fed from Key Vault in AKS); without it keys are ephemeral, which
/// is acceptable only for tests and single-run development.
/// </summary>
public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "DeNoise.RowSecrets.v1";
    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);
    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
