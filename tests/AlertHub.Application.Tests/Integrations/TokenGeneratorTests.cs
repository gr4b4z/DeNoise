using AlertHub.Application.Integrations;

namespace AlertHub.Application.Tests.Integrations;

[Trait("Category", "Unit")]
public sealed class TokenGeneratorTests
{
    [Fact]
    public void Key_ids_are_twelve_unambiguous_url_safe_characters()
    {
        for (var i = 0; i < 100; i++)
        {
            var keyId = TokenGenerator.NewKeyId();
            keyId.Should().HaveLength(TokenGenerator.KeyIdLength);
            keyId.Should().MatchRegex("^[abcdefghijkmnpqrstuvwxyz23456789]+$");
        }
    }

    [Fact]
    public void Secrets_are_32_random_bytes_in_base64url_and_unique()
    {
        var secrets = Enumerable.Range(0, 100).Select(_ => TokenGenerator.NewSecret()).ToList();
        secrets.Should().OnlyHaveUniqueItems();
        secrets.Should().OnlyContain(s => s.Length == 43 && !s.Contains('+') && !s.Contains('/') && !s.Contains('='));
    }
}
