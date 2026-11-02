using System.Net;
using DeNoise.Application.Ingest;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Tests.Ingest;

public sealed class IngestCredentialPolicyTests
{
    private static Integration WithCapabilities(string capabilities) => new()
    {
        Name = "azure",
        Type = IntegrationTypes.AzureMonitor,
        AccessScope = "scope-a",
        IngestKeyId = "abcdefghjkmn",
        IngestTokenHash = "hash",
        Capabilities = capabilities,
    };

    [Theory]
    [InlineData("""{"path_credential":true}""", true)]
    [InlineData("""{"path_credential": true, "state_query": true}""", true)]
    [InlineData("""{"path_credential":false}""", false)]
    [InlineData("""{"state_query":true}""", false)]
    [InlineData("{}", false)]
    [InlineData("", false)]
    [InlineData("not json", false)]
    [InlineData("""{"path_credential":"true"}""", false)]
    public void Capability_opt_in_is_explicit(string capabilities, bool expected)
        => IngestCredentialPolicy.AllowsPathCredential(WithCapabilities(capabilities)).Should().Be(expected);

    [Fact]
    public void Empty_allow_list_permits_everything()
    {
        IngestCredentialPolicy.PermitsAddress(null, IPAddress.Parse("203.0.113.7")).Should().BeTrue();
        IngestCredentialPolicy.PermitsAddress([], IPAddress.Parse("203.0.113.7")).Should().BeTrue();
    }

    [Theory]
    [InlineData("203.0.113.7", true)]
    [InlineData("203.0.113.8", false)]
    public void Single_address_entries_match_exactly(string candidate, bool expected)
        => IngestCredentialPolicy.PermitsAddress(["203.0.113.7"], IPAddress.Parse(candidate)).Should().Be(expected);

    [Theory]
    [InlineData("203.0.113.0", true)]
    [InlineData("203.0.113.255", true)]
    [InlineData("203.0.114.1", false)]
    public void Cidr_entries_match_the_network(string candidate, bool expected)
        => IngestCredentialPolicy.PermitsAddress(["203.0.113.0/24"], IPAddress.Parse(candidate)).Should().Be(expected);

    [Fact]
    public void Ipv4_mapped_addresses_are_compared_as_ipv4()
        => IngestCredentialPolicy.PermitsAddress(["203.0.113.0/24"], IPAddress.Parse("::ffff:203.0.113.9")).Should().BeTrue();

    [Fact]
    public void Ipv6_entries_match_ipv6_sources()
        => IngestCredentialPolicy.PermitsAddress(["2001:db8::/32"], IPAddress.Parse("2001:db8::1")).Should().BeTrue();

    [Fact]
    public void A_missing_source_address_is_refused_when_a_list_is_configured()
        => IngestCredentialPolicy.PermitsAddress(["203.0.113.0/24"], null).Should().BeFalse();

    [Theory]
    [InlineData("nonsense")]
    [InlineData("203.0.113.0/notanumber")]
    [InlineData("")]
    public void Malformed_entries_never_permit(string entry)
        => IngestCredentialPolicy.PermitsAddress([entry], IPAddress.Parse("203.0.113.7")).Should().BeFalse();
}
