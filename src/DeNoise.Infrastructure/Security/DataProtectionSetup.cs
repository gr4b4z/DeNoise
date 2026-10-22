using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeNoise.Infrastructure.Security;

/// <summary>
/// The Data Protection key ring shared by every host (ADR-11, ADR-18). Three sources, in order of preference:
/// <list type="bullet">
/// <item><c>DataProtection:Keys</c> (<c>DataProtection__Keys__0</c>, <c>__1</c>, …): key XML documents produced by
/// <c>DeNoise.Migrator generate-dataprotection-key</c>, kept in Key Vault and passed as secret environment variables.
/// The ring is read-only.</item>
/// <item><c>DataProtection:KeysPath</c>: a directory of key files, shared by all hosts (a volume). Read-only when
/// <c>DataProtection:ReadOnly</c> is true, for example a mounted Secret.</item>
/// <item>Neither: an in-memory ring per process, fit only for tests and single-run development.</item>
/// </list>
/// A read-only ring never generates keys: with no key at all, the first protect or unprotect fails instead of silently
/// creating a key no other host knows.
/// </summary>
public static class DataProtectionSetup
{
    public const string ApplicationName = "DeNoise";
    public const string Section = "DataProtection";

    public static IServiceCollection AddDeNoiseDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var keys = ConfiguredKeys(section.GetSection("Keys"));
        var keysPath = section["KeysPath"];
        var readOnly = section.GetValue<bool>("ReadOnly");

        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        if (keys.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(keysPath))
            {
                throw new InvalidOperationException("Configure either DataProtection:Keys or DataProtection:KeysPath, not both.");
            }
            var repository = new ConfigurationXmlRepository(keys);
            services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
            readOnly = true;
        }
        else if (!string.IsNullOrWhiteSpace(keysPath))
        {
            builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }
        if (readOnly) builder.DisableAutomaticKeyGeneration();
        return services;
    }

    // `DataProtection:Keys` is either one value or an array (`Keys:0`, `Keys:1`, … from `DataProtection__Keys__0`).
    private static List<string> ConfiguredKeys(IConfigurationSection keys)
    {
        if (!string.IsNullOrWhiteSpace(keys.Value)) return [keys.Value];
        return keys.GetChildren()
            .OrderBy(c => int.TryParse(c.Key, out var n) ? n : int.MaxValue)
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();
    }
}

/// <summary>A read-only key repository over key XML documents taken from configuration (<c>DataProtection:Keys</c>).</summary>
public sealed class ConfigurationXmlRepository : IXmlRepository
{
    private readonly IReadOnlyList<XElement> _elements;

    public ConfigurationXmlRepository(IEnumerable<string> keys)
    {
        _elements = keys.Select(Parse).ToList();
    }

    public IReadOnlyCollection<XElement> GetAllElements() => _elements.Select(e => new XElement(e)).ToList();

    public void StoreElement(XElement element, string friendlyName) =>
        throw new InvalidOperationException(
            "The Data Protection key ring comes from DataProtection:Keys and is read-only. Generate a key with "
            + "`DeNoise.Migrator generate-dataprotection-key` and add it to the configured keys (ADR-18).");

    // Never echo the value: it is key material.
    private static XElement Parse(string xml, int index)
    {
        XElement element;
        try
        {
            element = XElement.Parse(xml.Trim());
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"DataProtection:Keys:{index} is not an XML document (line {ex.LineNumber}).", ex);
        }
        if (element.Name.LocalName != "key")
        {
            throw new InvalidOperationException($"DataProtection:Keys:{index} is not a Data Protection key (root element <{element.Name.LocalName}>, expected <key>).");
        }
        return element;
    }
}

/// <summary>
/// Creates one Data Protection key as an XML document, the format the key ring stores (ADR-18). The master key is
/// <b>not</b> encrypted: the document belongs in Key Vault and nowhere else.
/// </summary>
public static class DataProtectionKeyGenerator
{
    public static string Generate(DateTimeOffset activation, DateTimeOffset expiration)
    {
        if (expiration <= activation) throw new ArgumentException("The expiration must be after the activation.", nameof(expiration));
        var repository = new CapturingXmlRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(DataProtectionSetup.ApplicationName);
        // An explicit repository without an encryptor: no DPAPI/certificate wrapping, whatever the machine.
        services.Configure<KeyManagementOptions>(o => o.XmlRepository = repository);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IKeyManager>().CreateNewKey(activation, expiration);
        return repository.Elements.Single().ToString(SaveOptions.None);
    }

    private sealed class CapturingXmlRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = [];
        public IReadOnlyCollection<XElement> GetAllElements() => Elements.ToList();
        public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
    }
}
