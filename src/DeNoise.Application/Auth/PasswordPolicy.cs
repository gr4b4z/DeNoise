namespace DeNoise.Application.Auth;

public sealed class LocalAuthOptions
{
    public const string Section = "Auth:Local";
    /// <summary>Bootstrap <c>admin</c> password (Helm secret). Consumed on first start when no user exists; never logged.</summary>
    public string? BootstrapPassword { get; set; }
    public int PasswordMinLength { get; set; } = 12;
    public int LockoutFailures { get; set; } = 10;
    public TimeSpan LockoutWindow { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan SessionSliding { get; set; } = TimeSpan.FromHours(8);
    public TimeSpan SessionAbsolute { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Self-service reset by e-mail is only offered when SMTP is configured and this is true.</summary>
    public bool SelfServiceResetEnabled { get; set; }
}

/// <summary>ADR-14 password rules: minimum length and a bundled breached-password list (starter list; extend from HIBP offline data before pilot).</summary>
public static class PasswordPolicy
{
    // The most common leaked passwords (lower-cased). Comparison is case-insensitive and ignores surrounding whitespace.
    private static readonly HashSet<string> Breached = new(StringComparer.OrdinalIgnoreCase)
    {
        "123456", "password", "123456789", "12345678", "12345", "qwerty", "1234567", "111111", "1234567890", "123123", "abc123", "1234", "password1",
        "iloveyou", "1q2w3e4r", "000000", "qwerty123", "zaq12wsx", "dragon", "sunshine", "princess", "letmein", "654321", "monkey", "27653",
        "1qaz2wsx", "123321", "qwertyuiop", "superman", "asdfghjkl", "welcome", "admin", "administrator", "passw0rd", "password123", "p@ssw0rd",
        "p@ssword", "changeme", "letmein123", "welcome1", "welcome123", "qwerty1234", "1q2w3e4r5t", "trustno1", "football", "baseball", "master",
        "hello123", "freedom", "whatever", "starwars", "shadow", "michael", "jennifer", "computer", "michelle", "summer2024", "summer2025",
        "winter2025", "spring2026", "summer2026", "denoise", "denoise123", "denoise", "softwareone", "softwareone1", "softwareone123",
        "password!", "password1!", "passw0rd!", "qwerty!", "abcd1234", "abcd12345", "12341234", "1234qwer", "qwer1234", "asdf1234", "zxcvbnm",
        "iloveyou1", "monkey123", "dragon123", "123qwe", "123abc", "a123456", "aa123456", "112233", "121212", "123654", "159753", "666666",
        "777777", "888888", "987654321", "azerty", "azerty123", "loveme", "secret", "secret123", "temp1234", "temporary", "default", "guest",
        "guest123", "test", "test123", "test1234", "testing", "testing123", "demo", "demo123", "user", "user123", "root", "toor", "oracle",
        "postgres", "mysql", "changeme1", "changeme123", "pass1234", "pass123", "pass@123", "pass@word1", "welcome@123", "admin123", "admin@123",
        "admin1234", "administrator1", "rootroot", "qazwsx", "qazwsxedc", "1qazxsw2", "zaq1xsw2", "poland", "polska", "warszawa", "krakow",
        "wroclaw", "cloud123", "azure123", "microsoft", "office365", "windows", "linux", "ubuntu", "docker", "kubernetes", "correcthorsebatterystaple",
    };

    public static IReadOnlyList<string> Validate(string password, int minLength)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(password) || password.Length < minLength) problems.Add($"must be at least {minLength} characters");
        if (password.Trim().Length != password.Length) problems.Add("must not start or end with whitespace");
        if (IsBreached(password)) problems.Add("appears in a list of breached passwords");
        return problems;
    }

    public static bool IsBreached(string password)
    {
        var p = password.Trim();
        if (Breached.Contains(p)) return true;
        // common suffix noise: trailing digits/punctuation on a breached word
        var stripped = p.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '!', '?', '.', '#', '$', '@', '_', '-');
        return stripped.Length >= 4 && stripped.Length < p.Length && Breached.Contains(stripped);
    }

    /// <summary>Temporary passwords for admin-initiated resets: 20 characters from an unambiguous alphabet, shown once.</summary>
    public static string GenerateTemporary()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789-.";
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}
