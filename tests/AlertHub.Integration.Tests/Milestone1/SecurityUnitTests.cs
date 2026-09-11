using AlertHub.Infrastructure.Security;
using AlertHub.Workers;

namespace AlertHub.Integration.Tests.Milestone1;

/// <summary>Unit-category tests for infrastructure pieces that need no database.</summary>
[Trait("Category", "Unit")]
public sealed class SecurityUnitTests
{
    private readonly Argon2SecretHasher _hasher = new();

    [Fact]
    public void Token_hash_round_trips_and_encodes_its_parameters()
    {
        var hash = _hasher.HashToken("s3cret-token");
        hash.Should().StartWith("$argon2id$v=19$m=16384,t=2,p=1$");
        _hasher.Verify("s3cret-token", hash).Should().BeTrue();
        _hasher.Verify("s3cret-tokeN", hash).Should().BeFalse();
        _hasher.HashToken("s3cret-token").Should().NotBe(hash, "salts are per row");
    }

    [Fact]
    public void Password_hash_uses_the_adr14_parameters()
    {
        var hash = _hasher.HashPassword("correct horse battery staple");
        hash.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=1$");
        _hasher.Verify("correct horse battery staple", hash).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2i$v=19$m=16384,t=2,p=1$c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=0,t=2,p=1$c2FsdA$aGFzaA")]
    [InlineData("$argon2id$v=19$m=16384,t=2,p=1$not base64!$aGFzaA")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        _hasher.Verify("anything", encoded).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true, true, true)]
    [InlineData("", true, true, true)]
    [InlineData("processing", true, false, false)]
    [InlineData("scheduler, dispatcher", false, true, true)]
    [InlineData("Processing;Dispatcher", true, false, true)]
    public void Worker_roles_parse(string? value, bool processing, bool scheduler, bool dispatcher)
    {
        var roles = WorkerRoles.Parse(value);
        roles.Should().Be(new WorkerRoles(processing, scheduler, dispatcher));
    }

    [Fact]
    public void Unknown_worker_role_is_rejected()
    {
        var act = () => WorkerRoles.Parse("processing,mailer");
        act.Should().Throw<ArgumentException>().WithMessage("*mailer*");
    }
}
