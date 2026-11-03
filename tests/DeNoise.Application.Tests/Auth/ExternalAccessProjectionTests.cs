using DeNoise.Application.Auth;
using DeNoise.Domain.Users;

namespace DeNoise.Application.Tests.Auth;

/// <summary>M11b / ADR-15 projection: roles claim → roleMap, groups claim → scopeMap, overage, returnTo validation, options.</summary>
[Trait("Category", "Unit")]
public sealed class ExternalAccessProjectionTests
{
    private static ExternalPrincipal Principal(string[] roles, string[]? groups = null, bool overage = false)
        => new("https://login.microsoftonline.com/t/v2.0", "oid-1", null, null, null, roles, groups ?? [], overage);

    private static OidcAuthOptions Options(Dictionary<string, string>? roleMap = null) => new()
    {
        RoleMap = roleMap ?? new Dictionary<string, string>(StringComparer.Ordinal),
        ScopeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["G-MPT"] = "mpt", ["g-prism"] = "prism", ["g-prism-2"] = "prism" },
    };

    [Fact]
    public void Default_role_map_covers_the_four_app_roles_and_orders_roles_like_the_matrix()
    {
        var access = ExternalAccessProjection.Project(Principal(["DeNoise.PlatformAdmin", "DeNoise.Viewer", "DeNoise.Operator", "DeNoise.IntegrationAdmin"]), Options());
        access.Roles.Should().Equal(Roles.Viewer, Roles.Operator, Roles.IntegrationAdmin, Roles.PlatformAdmin);
        access.UnmappedRoles.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_app_roles_grant_nothing_and_are_reported()
    {
        var access = ExternalAccessProjection.Project(Principal(["DeNoise.Operator", "Other.App.Admin", "denoise.operator"]), Options());
        access.Roles.Should().Equal(Roles.Operator);
        access.UnmappedRoles.Should().Equal("Other.App.Admin", "denoise.operator");
    }

    [Fact]
    public void A_configured_role_map_replaces_the_default_and_cannot_invent_roles()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { ["Ops"] = Roles.Operator, ["Root"] = "superuser" };
        var access = ExternalAccessProjection.Project(Principal(["Ops", "Root", "DeNoise.PlatformAdmin"]), Options(map));
        access.Roles.Should().Equal(Roles.Operator);
        access.UnmappedRoles.Should().Equal("Root", "DeNoise.PlatformAdmin");
    }

    [Fact]
    public void Groups_map_to_scopes_case_insensitively_deduplicated_and_unmapped_groups_are_ignored()
    {
        var access = ExternalAccessProjection.Project(Principal(["DeNoise.Viewer"], ["g-mpt", "g-prism", "g-prism-2", "g-unrelated"]), Options());
        access.Scopes.Should().Equal("mpt", "prism");
    }

    [Fact]
    public void Groups_overage_projects_no_scopes_but_keeps_roles()
    {
        var access = ExternalAccessProjection.Project(Principal(["DeNoise.Operator"], ["g-mpt"], overage: true), Options());
        access.GroupsOverage.Should().BeTrue();
        access.Scopes.Should().BeEmpty("membership is unknown: least privilege");
        access.Roles.Should().Equal([Roles.Operator], "roles ride on app roles and are never subject to overage");
    }

    [Theory]
    [InlineData("/queue", "/queue")]
    [InlineData("/episodes/0193?tab=timeline#notes", "/episodes/0193?tab=timeline#notes")]
    [InlineData("/history?q=a%20b", "/history?q=a%20b")]
    [InlineData(null, "/queue")]
    [InlineData("", "/queue")]
    [InlineData("queue", "/queue")]
    [InlineData("https://evil.example/", "/queue")]
    [InlineData("//evil.example/x", "/queue")]
    [InlineData("/\\evil.example", "/queue")]
    [InlineData("/x\\y", "/queue")]
    [InlineData("javascript:alert(1)", "/queue")]
    [InlineData("/queue\r\nSet-Cookie: x=1", "/queue")]
    [InlineData("/ queue", "/queue")]
    [InlineData("/login", "/queue")]
    [InlineData("/login/change-password", "/queue")]
    [InlineData("/auth/oidc/start?returnTo=/queue", "/queue")]
    public void ReturnTo_must_be_a_local_path_outside_the_sign_in_flow(string? returnTo, string expected)
        => ReturnToPolicy.Sanitize(returnTo).Should().Be(expected);

    [Fact]
    public void ReturnTo_longer_than_2048_characters_is_replaced()
        => ReturnToPolicy.Sanitize("/" + new string('a', 2048)).Should().Be("/queue");

    [Fact]
    public void Options_are_only_validated_when_enabled()
        => new OidcAuthOptions { Enabled = false, Credential = "nonsense" }.Validate().Should().BeEmpty();

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/v2.0", "single tenant")]
    [InlineData("https://login.microsoftonline.com/organizations/v2.0", "single tenant")]
    [InlineData("http://login.microsoftonline.com/0000/v2.0", "https")]
    [InlineData("not a url", "absolute URL")]
    public void Authority_must_be_a_single_tenant_https_url(string authority, string problem)
        => new OidcAuthOptions { Enabled = true, Authority = authority, ClientId = "c" }.Validate().Should().Contain(p => p.Contains(problem));

    [Fact]
    public void Secret_credential_requires_a_secret_and_unknown_roles_and_graph_lookup_are_refused()
    {
        var problems = new OidcAuthOptions
        {
            Enabled = true,
            Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0",
            ClientId = "c",
            Credential = OidcCredentials.Secret,
            RoleMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["X"] = "root" },
            GraphGroupLookup = true,
        }.Validate();
        problems.Should().Contain(p => p.Contains("ClientSecret")).And.Contain(p => p.Contains("unknown roles: root")).And.Contain(p => p.Contains("GraphGroupLookup"));
    }

    [Fact]
    public void A_complete_federated_configuration_is_valid()
        => new OidcAuthOptions { Enabled = true, Authority = "https://login.microsoftonline.com/11111111-1111-1111-1111-111111111111/v2.0", ClientId = "c" }
            .Validate().Should().BeEmpty();
}
