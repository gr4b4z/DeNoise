using System.Xml.Linq;
using DeNoise.Infrastructure.Security;
using DeNoise.Migrator;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeNoise.Integration.Tests.DataProtection;

/// <summary>
/// ADR-18: the key ring is generated once (<c>DeNoise.Migrator generate-dataprotection-key</c>), kept in Key Vault and
/// handed to every host as <c>DataProtection:Keys</c>; the hosts never create keys of their own. No database needed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataProtectionKeyRingTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<DirectoryInfo> _directories = [];
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        foreach (var p in _providers) p.Dispose();
        foreach (var d in _directories) d.Delete(recursive: true);
    }

    [Fact]
    public void Generated_key_is_an_unencrypted_key_document_with_the_requested_dates()
    {
        var xml = DataProtectionKeyGenerator.Generate(Now, Now.AddDays(3650));
        var key = XElement.Parse(xml);

        key.Name.LocalName.Should().Be("key");
        DateTimeOffset.Parse(key.Element("activationDate")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(Now, TimeSpan.FromSeconds(1));
        DateTimeOffset.Parse(key.Element("expirationDate")!.Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(Now.AddDays(3650), TimeSpan.FromSeconds(1));
        key.Descendants("value").Single().Value.Should().NotBeNullOrWhiteSpace();
        key.Descendants().Should().NotContain(e => e.Name.LocalName == "encryptedSecret", "the document must be readable on any host, so it is not wrapped");
    }

    [Fact]
    public void Hosts_configured_with_the_same_key_read_each_others_payloads()
    {
        var key = DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(30));
        var api = Protector(("DataProtection:Keys:0", key));
        var workers = Protector(("DataProtection:Keys:0", key));
        var stranger = Protector(("DataProtection:Keys:0", DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(30))));

        var payload = api.Protect("https://hooks.example/secret-path");

        workers.Unprotect(payload).Should().Be("https://hooks.example/secret-path");
        var foreign = () => stranger.Unprotect(payload);
        foreign.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }

    [Fact]
    public void A_single_value_is_accepted_as_well_as_an_array()
    {
        var key = DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(30));
        var array = Protector(("DataProtection:Keys:0", key));
        var single = Protector(("DataProtection:Keys", key));

        single.Unprotect(array.Protect("x")).Should().Be("x");
    }

    [Fact]
    public void An_added_key_becomes_the_default_and_the_old_one_still_decrypts()
    {
        var oldKey = DataProtectionKeyGenerator.Generate(Now.AddDays(-10), Now.AddDays(30));
        var newKey = DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(60));
        var before = Protector(("DataProtection:Keys:0", oldKey));
        var after = Protector(("DataProtection:Keys:0", oldKey), ("DataProtection:Keys:1", newKey));
        var newOnly = Protector(("DataProtection:Keys:0", newKey));

        var written = before.Protect("old row");
        after.Unprotect(written).Should().Be("old row");
        newOnly.Unprotect(after.Protect("new row")).Should().Be("new row", "after rotation the most recently activated key protects new data");
    }

    [Fact]
    public void An_expired_configured_key_no_longer_protects_so_a_new_key_must_be_added_before_expiry()
    {
        var expired = DataProtectionKeyGenerator.Generate(Now.AddDays(-20), Now.AddDays(-10));
        var expiredOnly = Protector(("DataProtection:Keys:0", expired));
        var rotated = Protector(("DataProtection:Keys:0", expired), ("DataProtection:Keys:1", DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(30))));

        var act = () => expiredOnly.Protect("x");

        act.Should().Throw<System.Security.Cryptography.CryptographicException>("the ring never creates a successor key itself");
        rotated.Unprotect(rotated.Protect("x")).Should().Be("x");
    }

    [Fact]
    public void A_read_only_directory_without_keys_fails_instead_of_creating_one()
    {
        var dir = TempDirectory();
        var host = Protector(("DataProtection:KeysPath", dir.FullName), ("DataProtection:ReadOnly", "true"));

        var act = () => host.Protect("x");

        act.Should().Throw<Exception>();
        dir.GetFiles().Should().BeEmpty("a read-only ring never writes a key");
    }

    [Fact]
    public void A_read_only_directory_with_a_generated_key_file_works()
    {
        var dir = TempDirectory();
        var key = DataProtectionKeyGenerator.Generate(Now.AddMinutes(-1), Now.AddDays(30));
        File.WriteAllText(Path.Combine(dir.FullName, "key-1.xml"), key);
        var mounted = Protector(("DataProtection:KeysPath", dir.FullName), ("DataProtection:ReadOnly", "true"));
        var fromConfig = Protector(("DataProtection:Keys:0", key));

        fromConfig.Unprotect(mounted.Protect("x")).Should().Be("x");
        dir.GetFiles().Should().HaveCount(1);
    }

    [Fact]
    public void Keys_and_KeysPath_together_are_refused()
    {
        var key = DataProtectionKeyGenerator.Generate(Now, Now.AddDays(30));
        var act = () => Protector(("DataProtection:Keys:0", key), ("DataProtection:KeysPath", TempDirectory().FullName));

        act.Should().Throw<InvalidOperationException>().WithMessage("*either DataProtection:Keys or DataProtection:KeysPath*");
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<repository><key/></repository>")]
    public void A_malformed_key_is_refused_without_echoing_it(string value)
    {
        var act = () => Protector(("DataProtection:Keys:0", value));

        act.Should().Throw<InvalidOperationException>().Which.Message.Should().StartWith("DataProtection:Keys:0").And.NotContain(value);
    }

    [Fact]
    public void Migrator_command_writes_the_key_to_a_file_and_keeps_stdout_empty()
    {
        var path = Path.Combine(TempDirectory().FullName, "key.xml");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

        var exit = DataProtectionKeyCommand.Run([DataProtectionKeyCommand.Name, "--lifetime-days", "365", "--activate-in-days", "2", "--out", path], stdout, stderr, time);

        exit.Should().Be(0);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("active 2026-10-24, expires 2027-10-24").And.NotContain("<value>");
        XElement.Parse(File.ReadAllText(path)).Element("activationDate")!.Value.Should().StartWith("2026-10-24");
    }

    [Theory]
    [InlineData("--lifetime-days", "0")]
    [InlineData("--activate-in-days", "-1")]
    [InlineData("--out", "")]
    [InlineData("--bogus", "1")]
    public void Migrator_command_rejects_bad_arguments(string option, string value)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        DataProtectionKeyCommand.Run([DataProtectionKeyCommand.Name, option, value], stdout, stderr, TimeProvider.System).Should().Be(2);
        stdout.ToString().Should().BeEmpty();
    }

    private IDataProtector Protector(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDeNoiseDataProtection(configuration);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector(DataProtectionSecretProtector.Purpose);
    }

    private DirectoryInfo TempDirectory()
    {
        var dir = Directory.CreateTempSubdirectory("denoise-dp-");
        _directories.Add(dir);
        return dir;
    }
}
