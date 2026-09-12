using System.Text;
using DeNoise.Application.Ingest;
using DeNoise.Application.Notifications;
using DeNoise.Domain.Integrations;

namespace DeNoise.Application.Tests.Ingest;

[Trait("Category", "Unit")]
public sealed class IngestSignatureVerifierTests
{
    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext["enc:".Length..];
    }

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"a1","status":"OPEN"}""");
    private readonly IngestSignatureVerifier _verifier = new(new PlainProtector());

    private static Integration Atlas(string? secret, HmacConfig? config) => new()
    {
        IntegrationId = Guid.NewGuid(),
        Name = "atlas",
        Type = IntegrationTypes.Atlas,
        AccessScope = "s",
        IngestKeyId = "k",
        IngestTokenHash = "h",
        HmacSecretEnc = secret is null ? null : "enc:" + secret,
        HmacConfig = config?.ToJson(),
    };

    [Theory]
    [InlineData("sha1", "base64")]
    [InlineData("sha256", "hex")]
    [InlineData("sha256", "base64")]
    public void Valid_signature_is_accepted_in_every_algorithm_and_encoding(string algorithm, string encoding)
    {
        var config = new HmacConfig(algorithm, "X-MMS-Signature", encoding, Required: true);
        var signature = IngestSignatureVerifier.Sign(config, "s3cret", Body);
        _verifier.Verify(Atlas("s3cret", config), Body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MMS-Signature"] = signature }).Should().Be(SignatureVerdict.Valid);
    }

    [Fact]
    public void Tampered_body_wrong_secret_or_garbage_are_invalid()
    {
        var config = new HmacConfig();
        var signature = IngestSignatureVerifier.Sign(config, "s3cret", Body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-MMS-Signature"] = signature };
        _verifier.Verify(Atlas("s3cret", config), Encoding.UTF8.GetBytes("""{"id":"a1","status":"CLOSED"}"""), headers).Should().Be(SignatureVerdict.Invalid);
        _verifier.Verify(Atlas("other", config), Body, headers).Should().Be(SignatureVerdict.Invalid);
        _verifier.Verify(Atlas("s3cret", config), Body, new Dictionary<string, string> { ["X-MMS-Signature"] = "not base64 at all!" }).Should().Be(SignatureVerdict.Invalid);
    }

    [Fact]
    public void Missing_header_and_unconfigured_secret_are_reported_not_refused_here()
    {
        _verifier.Verify(Atlas("s3cret", new HmacConfig()), Body, new Dictionary<string, string>()).Should().Be(SignatureVerdict.Missing, "the endpoint decides by `required`");
        _verifier.Verify(Atlas(null, null), Body, new Dictionary<string, string>()).Should().Be(SignatureVerdict.NotConfigured);
    }

    [Fact]
    public void Config_round_trips_and_validates()
    {
        var parsed = HmacConfig.Parse("""{"algorithm":"SHA256","header":"X-Sig","encoding":"HEX","required":true}""");
        parsed.Should().Be(new HmacConfig("sha256", "X-Sig", "hex", true));
        HmacConfig.Parse(parsed!.ToJson()).Should().Be(parsed);
        HmacConfig.Parse(null).Should().BeNull();
        HmacConfig.Parse("not json").Should().BeNull();
        new HmacConfig("md5", "X Bad", "raw").Validate().Select(e => e.Path).Should().BeEquivalentTo(["$.hmac.algorithm", "$.hmac.encoding", "$.hmac.header"]);
    }
}
