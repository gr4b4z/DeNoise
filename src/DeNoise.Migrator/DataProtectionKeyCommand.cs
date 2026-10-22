using System.Globalization;
using DeNoise.Infrastructure.Security;

namespace DeNoise.Migrator;

/// <summary>
/// <c>generate-dataprotection-key [--lifetime-days N] [--activate-in-days N] [--out FILE]</c>: writes one new Data
/// Protection key as XML (ADR-18) to stdout or FILE, for Key Vault. Needs no database. Status lines go to stderr so
/// stdout can be piped straight into <c>az keyvault secret set --file</c>.
/// </summary>
public static class DataProtectionKeyCommand
{
    public const string Name = "generate-dataprotection-key";
    public const int DefaultLifetimeDays = 3650;

    public static bool Matches(string[] args) => args.Length > 0 && string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, TimeProvider time)
    {
        var lifetimeDays = DefaultLifetimeDays;
        var activateInDays = 0;
        string? outPath = null;
        for (var i = 1; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--lifetime-days" when TryDays(value, 1, out lifetimeDays):
                case "--activate-in-days" when TryDays(value, 0, out activateInDays):
                    i++;
                    break;
                case "--out" when !string.IsNullOrWhiteSpace(value):
                    outPath = value;
                    i++;
                    break;
                default:
                    stderr.WriteLine($"Unknown or invalid argument '{args[i]}'. Usage: {Name} [--lifetime-days N] [--activate-in-days N] [--out FILE]");
                    return 2;
            }
        }

        var activation = time.GetUtcNow().AddDays(activateInDays);
        var expiration = activation.AddDays(lifetimeDays);
        var xml = DataProtectionKeyGenerator.Generate(activation, expiration);
        if (outPath is null)
        {
            stdout.WriteLine(xml);
        }
        else
        {
            File.WriteAllText(outPath, xml);
        }
        var target = outPath is null ? string.Empty : ", written to " + outPath;
        stderr.WriteLine(FormattableString.Invariant($"Data Protection key generated: active {activation:yyyy-MM-dd}, expires {expiration:yyyy-MM-dd}{target}.")
            + " The master key is not encrypted: store it in Key Vault and delete any local copy.");
        return 0;
    }

    private static bool TryDays(string? value, int min, out int days) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out days) && days >= min;
}
