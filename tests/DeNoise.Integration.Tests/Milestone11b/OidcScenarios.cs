using System.Net;
using System.Text.Json;
using DeNoise.Application.Auth;
using DeNoise.Contracts;
using DeNoise.Domain.Users;
using DeNoise.Infrastructure.Auth;
using DeNoise.Integration.Tests.Milestone4;
using DeNoise.Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DeNoise.Integration.Tests.Milestone11b;

/// <summary>
/// M11b (docs/milestones/M11b-oidc-entra-id.md, ADR-15): every row of the milestone's Tests table, against a real mock
/// OIDC provider. App roles and groups are the claims the mock puts in the id_token; enforcement is the unchanged API.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class OidcScenarios(PostgresFixture postgres, MockOidcServer mock) : IClassFixture<MockOidcServer>, IAsyncLifetime
{
    private const string GroupA = "6f1c2a8e-0000-4000-8000-00000000000a";
    private const string GroupB = "6f1c2a8e-0000-4000-8000-00000000000b";
    private ApiFixture _api = null!;
    private OidcBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _api = await CreateApiAsync();
        _browser = new OidcBrowser(_api);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _api.DisposeAsync();
    }

    private Dictionary<string, string?> OidcSettings(string credential = OidcCredentials.Secret) => new()
    {
        ["Auth:Oidc:Enabled"] = "true",
        ["Auth:Oidc:Authority"] = mock.Authority(),
        ["Auth:Oidc:ClientId"] = MockOidcServer.ClientId,
        ["Auth:Oidc:Credential"] = credential,
        ["Auth:Oidc:ClientSecret"] = credential == OidcCredentials.Secret ? "test-client-secret" : null,
        ["Auth:Oidc:RequireHttpsMetadata"] = "false",
        [$"Auth:Oidc:ScopeMap:{GroupA}"] = "scope-a",
        [$"Auth:Oidc:ScopeMap:{GroupB}"] = "scope-b",
    };

    private Task<ApiFixture> CreateApiAsync(IDictionary<string, string?>? extra = null, Action<IServiceCollection>? configure = null, string credential = OidcCredentials.Secret)
    {
        var settings = OidcSettings(credential);
        if (extra is not null) foreach (var (k, v) in extra) settings[k] = v;
        return ApiFixture.CreateAsync(postgres, "oidc", settings, start: DateTimeOffset.UtcNow, configure: configure);
    }

    private static string NewOid() => Guid.NewGuid().ToString();

    private async Task<ApiClient> SessionAsync(SsoOutcome outcome)
    {
        if (outcome.SessionToken is null)
        {
            var reasons = (await AuditAsync(ExternalSignInService.AuditRefused)).Select(a => a.After.GetRawText());
            outcome.SessionToken.Should().NotBeNull($"sign-in should have succeeded, landed on {outcome.Location}; refusals: {string.Join(" | ", reasons)}");
        }
        var client = _api.Client();
        await client.UseSessionAsync(outcome.SessionToken!);
        return client;
    }

    private async Task<List<(string Action, JsonElement After)>> AuditAsync(string? action = null)
    {
        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        var rows = await db.AuditEntries.AsNoTracking().Where(a => action == null || a.Action == action).OrderBy(a => a.At).ThenBy(a => a.Id).ToListAsync();
        return rows.Select(r => (r.Action, r.After is null ? default : JsonDocument.Parse(r.After).RootElement.Clone())).ToList();
    }

    private async Task<List<string>> RefusalReasonsAsync()
        => (await AuditAsync(ExternalSignInService.AuditRefused)).Select(a => a.After.GetProperty("reason").GetString()!).ToList();

    private async Task<User?> UserByOidAsync(string oid)
    {
        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.ExternalId == oid);
    }

    private async Task<DeNoisePrincipal> AdminAsync()
    {
        await using var scope = _api.Factory.Services.CreateAsyncScope();
        var admin = await scope.ServiceProvider.GetRequiredService<IUserRepository>().FindByUsernameAsync("admin");
        return DeNoisePrincipal.ForSession(admin!, "test");
    }

    private static void ShouldBeRefused(SsoOutcome outcome)
    {
        outcome.Status.Should().Be(HttpStatusCode.Redirect);
        outcome.Location.Should().Be("/login?error=sso");
        outcome.SessionToken.Should().BeNull();
        outcome.Body.Should().BeEmpty();
    }

    [Fact]
    public async Task Scenario_FirstSignInWithAMappedAppRole_creates_the_user_projects_roles_and_scopes_issues_a_session_and_audits()
    {
        var oid = NewOid();
        var outcome = await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator", "Some.Other.App.Role"], [GroupA, "unmapped-group"],
            email: "nadia@contoso.example", name: "Nadia New", upn: "nadia@contoso.example"), returnTo: "/episodes?view=mine");

        outcome.Status.Should().Be(HttpStatusCode.Redirect);
        outcome.Location.Should().Be("/episodes?view=mine", "returnTo is honoured when it is a local path");
        using var client = await SessionAsync(outcome);
        var me = await client.GetAsync<MeResponse>("/api/v1/me");
        me!.Username.Should().Be("nadia@contoso.example");
        me.DisplayName.Should().Be("Nadia New");
        me.Roles.Should().Equal(Roles.Operator);
        me.Scopes.Should().Equal("scope-a");
        me.Credential.Should().Be("session");
        me.MustChangePassword.Should().BeFalse();

        var user = await UserByOidAsync(oid);
        user!.AuthProvider.Should().Be(AuthProviders.Oidc);
        user.ExternalIssuer.Should().Be(mock.Authority());
        user.PasswordHash.Should().BeNull();
        user.LocalOnly.Should().BeFalse();
        user.RolesProjectedAt.Should().BeCloseTo(_api.Time.GetUtcNow(), TimeSpan.FromMilliseconds(1));

        var audit = await AuditAsync();
        var provisioned = audit.Single(a => a.Action == ExternalSignInService.AuditProvisioned).After;
        provisioned.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(Roles.Operator);
        provisioned.GetProperty("unmappedRoles").EnumerateArray().Select(r => r.GetString()).Should().Equal("Some.Other.App.Role");
        audit.Should().Contain(a => a.Action == "auth.login" && a.After.GetProperty("provider").GetString() == "oidc");
        audit.Should().NotContain(a => a.Action == ExternalSignInService.AuditRefused);
    }

    [Fact]
    public async Task Scenario_SignInWithNoMappedRole_is_refused_audited_and_creates_no_user_row()
    {
        await using var before = PostgresFixture.CreateContext(_api.ConnectionString);
        var count = await before.Users.CountAsync();
        var oid = NewOid();

        var outcome = await _browser.SignInAsync(OidcBrowser.Claims(oid, ["Unrelated.Role"], [GroupA], email: "ghost@contoso.example"));

        ShouldBeRefused(outcome);
        await using var after = PostgresFixture.CreateContext(_api.ConnectionString);
        (await after.Users.CountAsync()).Should().Be(count, "no ghost rows");
        (await UserByOidAsync(oid)).Should().BeNull();
        var refusal = (await AuditAsync(ExternalSignInService.AuditRefused)).Single().After;
        refusal.GetProperty("reason").GetString().Should().Be(OidcRefusalReasons.NoMappedRole);
        refusal.GetProperty("externalId").GetString().Should().Be(oid);
    }

    [Fact]
    public async Task Scenario_RoleRemovedInEntra_user_signs_in_again_projection_lowers_the_role_and_every_PAT_narrows_on_the_next_call()
    {
        var oid = NewOid();
        var first = await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.IntegrationAdmin"], [GroupA], email: "ian@contoso.example"));
        using var session = await SessionAsync(first);
        var (status, token, _) = await session.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("ci", [], null));
        status.Should().Be(HttpStatusCode.Created);
        using var pat = _api.Client();
        pat.UseBearer(token!.Plaintext);
        (await pat.GetAsync<MeResponse>("/api/v1/me"))!.Permissions.Should().Contain(Permissions.TeamManage);
        using (var created = await pat.RawAsync(HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("ian-team", ["scope-a"]), csrf: false))
        {
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        // Entra: the IntegrationAdmin assignment is replaced by Viewer; the user signs in again.
        var second = await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Viewer"], [GroupA], email: "ian@contoso.example"));
        second.SessionToken.Should().NotBeNull();

        var narrowed = await pat.GetAsync<MeResponse>("/api/v1/me");
        narrowed!.Roles.Should().Equal(Roles.Viewer);
        narrowed.Permissions.Should().NotContain(Permissions.TeamManage).And.NotContain(Permissions.EpisodeAck);
        using (var refused = await pat.RawAsync(HttpMethod.Post, "/api/v1/teams", new CreateTeamRequest("ian-team-2", ["scope-a"]), csrf: false))
        {
            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the PAT narrowed with the owner's projected roles");
        }

        var projected = (await AuditAsync(ExternalSignInService.AuditRolesProjected)).Single().After;
        projected.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal(Roles.Viewer);
    }

    [Fact]
    public async Task Scenario_RoleRemovedInEntra_session_still_live_stays_authorised_until_disable_or_revoke()
    {
        // ADR-15 names this gap: the claim is read at sign-in only. Asserted so it cannot change silently.
        var oid = NewOid();
        var episode = await _api.OpenEpisodeAsync("live-1");
        using var session = await SessionAsync(await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA])));
        using var second = await SessionAsync(await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA])));

        // ... the Entra assignment is removed here; nothing reaches DeNoise until the next sign-in.
        _api.Time.Advance(TimeSpan.FromMinutes(30));
        (await session.GetAsync<MeResponse>("/api/v1/me"))!.Roles.Should().Equal(Roles.Operator);
        var detail = await session.GetAsync<EpisodeDetail>($"/api/v1/episodes/{episode}");
        using (var ack = await session.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{episode}/ack", new AckRequest(), ifMatch: detail!.Item.Version))
        {
            ack.StatusCode.Should().Be(HttpStatusCode.OK, "still authorised: the live session keeps the projected role");
        }

        // Revoking a session ends that one only.
        var sessions = await session.GetAsync<List<SessionSummary>>("/api/v1/me/sessions");
        var other = sessions!.Single(s => !s.Current);
        using (var revoke = await session.RawAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{other.Id}"))
        {
            revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        using (var revoked = await second.RawAsync(HttpMethod.Get, "/api/v1/me"))
        {
            revoked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // Disabling the account (the offboarding step) ends every session at once.
        await using (var scope = _api.Factory.Services.CreateAsyncScope())
        {
            var user = await UserByOidAsync(oid);
            await scope.ServiceProvider.GetRequiredService<UserService>().SetDisabledAsync(user!.UserId, true, await AdminAsync(), "test");
        }
        using var afterDisable = await session.RawAsync(HttpMethod.Get, "/api/v1/me");
        afterDisable.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        ShouldBeRefused(await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA])));
        (await RefusalReasonsAsync()).Should().Equal(OidcRefusalReasons.UserDisabled);
    }

    [Fact]
    public async Task Scenario_PreSsoAccountMatchedOnEmail_keeps_its_identifier_history_and_PATs_and_auth_provider_becomes_oidc()
    {
        using var password = await _api.LoginAsync("operator");
        var (_, token, _) = await password.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("pre-sso", ["episode.read"], null));
        var oid = NewOid();

        // Case differs from the stored olga@example.test: matching ignores case.
        var outcome = await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA, GroupB], email: "Olga@Example.test", name: "Olga Operator"));

        using var sso = await SessionAsync(outcome);
        var me = await sso.GetAsync<MeResponse>("/api/v1/me");
        me!.Id.Should().Be(_api.OperatorId, "same identifier");
        me.Username.Should().Be("operator");
        me.Teams.Select(t => t.Id).Should().Contain(_api.TeamA, "team membership is local and kept");
        me.Scopes.Should().Equal("scope-a", "scope-b");

        using var pat = _api.Client();
        pat.UseBearer(token!.Plaintext);
        (await pat.GetAsync<MeResponse>("/api/v1/me"))!.Id.Should().Be(_api.OperatorId, "PATs survive the link");

        var user = await UserByOidAsync(oid);
        user!.UserId.Should().Be(_api.OperatorId);
        user.AuthProvider.Should().Be(AuthProviders.Oidc);
        user.PasswordHash.Should().BeNull("the password is retired once the account is SSO");
        using var retired = await _api.Client().LoginAsync("operator", ApiFixture.UserPassword);
        retired.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var projected = (await AuditAsync(ExternalSignInService.AuditRolesProjected)).Single().After;
        projected.GetProperty("matchedBy").GetString().Should().Be("email");
        projected.GetProperty("authProvider").GetString().Should().Be(AuthProviders.Oidc);
        (await AuditAsync(ExternalSignInService.AuditProvisioned)).Should().BeEmpty();

        // The next sign-in matches on (issuer, oid), not e-mail.
        using var again = await SessionAsync(await _browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA, GroupB], email: "changed@contoso.example")));
        (await again.GetAsync<MeResponse>("/api/v1/me"))!.Id.Should().Be(_api.OperatorId);
    }

    [Fact]
    public async Task Scenario_LocalOnlyAccount_is_not_projected_and_password_sign_in_still_works_with_local_sign_in_disabled()
    {
        await using var local = await CreateApiAsync(new Dictionary<string, string?> { ["Auth:Local:Enabled"] = "false" });
        await using (var db = PostgresFixture.CreateContext(local.ConnectionString))
        {
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            admin.LocalOnly.Should().BeTrue("the bootstrap admin is the break-glass account");
            admin.Email = "breakglass@contoso.example";
            await db.SaveChangesAsync();
        }

        using var providers = local.Client();
        (await providers.GetAsync<ProvidersResponse>("/auth/providers"))!.Local.Should().BeFalse();

        // Password: the local_only admin gets in; an ordinary local account gets the same 401 as a wrong password.
        using var breakGlass = local.Client();
        (await breakGlass.LoginAsync("admin", ApiFixture.AdminPassword)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var ordinary = await local.Client().LoginAsync("viewer", ApiFixture.UserPassword);
        using var wrong = await local.Client().LoginAsync("viewer", "not-the-password-1");
        ordinary.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ordinary.Content.ReadAsStringAsync()).Should().Be((await wrong.Content.ReadAsStringAsync()).Replace(TraceId(wrong), TraceId(ordinary), StringComparison.Ordinal));

        // SSO: a token carrying the break-glass e-mail never takes the account over.
        using var browser = new OidcBrowser(local);
        ShouldBeRefused(await browser.SignInAsync(OidcBrowser.Claims(NewOid(), ["DeNoise.PlatformAdmin"], [], email: "BreakGlass@contoso.example")));
        await using (var db = PostgresFixture.CreateContext(local.ConnectionString))
        {
            var admin = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "admin");
            admin.AuthProvider.Should().Be(AuthProviders.Local);
            admin.ExternalId.Should().BeNull();
            var reason = JsonDocument.Parse((await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == ExternalSignInService.AuditRefused)).After!).RootElement.GetProperty("reason").GetString();
            reason.Should().Be(OidcRefusalReasons.EmailConflict);
        }

        // A platform_admin flags an SSO account local_only (admin API); its roles then stay local whatever the token says.
        var oid = NewOid();
        var first = await browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA]));
        first.SessionToken.Should().NotBeNull();
        using var admin2 = local.Client();
        await admin2.LoginAsync("admin", ApiFixture.AdminPassword);
        using (var change = await admin2.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Break-glass-passphrase-2026"), idempotency: false))
        {
            change.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
        Guid userId;
        await using (var db = PostgresFixture.CreateContext(local.ConnectionString)) userId = (await db.Users.SingleAsync(u => u.ExternalId == oid)).UserId;
        using (var put = await admin2.RawAsync(HttpMethod.Put, $"/api/v1/users/{userId}", new UpdateUserRequest(null, null, [Roles.IntegrationAdmin], null, LocalOnly: true)))
        {
            put.StatusCode.Should().Be(HttpStatusCode.OK);
            var summary = JsonSerializer.Deserialize<UserSummary>(await put.Content.ReadAsStringAsync(), ApiClient.Json)!;
            summary.LocalOnly.Should().BeTrue();
            summary.AuthProvider.Should().Be(AuthProviders.Oidc);
            summary.RolesProjectedAt.Should().NotBeNull();
        }

        var again = await browser.SignInAsync(OidcBrowser.Claims(oid, ["DeNoise.Viewer"], [GroupB]));
        using var session = local.Client();
        await session.UseSessionAsync(again.SessionToken!);
        var me = await session.GetAsync<MeResponse>("/api/v1/me");
        me!.Roles.Should().Equal([Roles.IntegrationAdmin], "local_only accounts are never projected");
        me.Scopes.Should().Equal("scope-a");
    }

    [Fact]
    public async Task Scenario_TamperedState_ReplayedCode_WrongAudience_ExpiredToken_are_each_refused_distinctly_in_the_audit_and_identically_to_the_caller()
    {
        var oid = NewOid();
        var claims = OidcBrowser.Claims(oid, ["DeNoise.Operator"], [GroupA]);
        var outcomes = new List<SsoOutcome>();

        // Tampered state: the state parameter no longer matches the sealed cookie.
        var (url, cookie) = await _browser.StartAsync();
        var query = await OidcBrowser.AuthorizeAsync(url, claims);
        outcomes.Add(await _browser.CallbackAsync(query.Replace("state=", "state=x", StringComparison.Ordinal), cookie));
        // ... and a forged cookie with the right state.
        outcomes.Add(await _browser.CallbackAsync(query, cookie[..^4] + "AAAA"));
        // ... and no cookie at all.
        outcomes.Add(await _browser.CallbackAsync(query, null));

        // Replayed code: a completed callback presented again, cookie included.
        var ok = await _browser.SignInAsync(claims);
        ok.SessionToken.Should().NotBeNull();
        outcomes.Add(await _browser.CallbackAsync(ok.CallbackQuery, ok.StateCookie));

        // Wrong audience: the provider issued the token for another application.
        var wrongAudience = new Dictionary<string, object>(claims) { ["aud"] = "another-application" };
        outcomes.Add(await _browser.SignInAsync(wrongAudience));

        // Identity provider error (e.g. the user is not assigned: AADSTS50105 arrives as error=access_denied).
        var (url2, cookie2) = await _browser.StartAsync();
        var state2 = System.Web.HttpUtility.ParseQueryString(url2.Query)["state"];
        outcomes.Add(await _browser.CallbackAsync($"?error=access_denied&error_description=AADSTS50105&state={state2}", cookie2));

        // Expired token: DeNoise's clock is past the token's exp + skew, while the sign-in itself is fresh.
        _api.Time.Advance(TimeSpan.FromHours(2));
        outcomes.Add(await _browser.SignInAsync(claims));

        foreach (var outcome in outcomes) ShouldBeRefused(outcome);
        outcomes.Select(o => (o.Status, o.Location, o.Body)).Distinct().Should().HaveCount(1, "every failure looks the same to the caller");
        // The frozen clock stamps the rows identically, so compare as a multiset: one distinct reason per failure.
        (await RefusalReasonsAsync()).Should().BeEquivalentTo([
            OidcRefusalReasons.StateInvalid, OidcRefusalReasons.StateInvalid, OidcRefusalReasons.StateMissing,
            OidcRefusalReasons.StateReplayed, OidcRefusalReasons.InvalidAudience, OidcRefusalReasons.IdpError, OidcRefusalReasons.TokenExpired]);
        var audits = await AuditAsync(ExternalSignInService.AuditRefused);
        foreach (var (_, after) in audits)
        {
            after.GetRawText().Should().NotContain("code=").And.NotContain(ok.SessionToken!).And.NotContain("test-client-secret");
        }
    }

    [Theory]
    [InlineData("https://evil.example/queue")]
    [InlineData("//evil.example/queue")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:alert(1)")]
    public async Task Scenario_ReturnToPointingOffHost_is_ignored_and_the_redirect_goes_to_the_queue(string returnTo)
    {
        var outcome = await _browser.SignInAsync(OidcBrowser.Claims(NewOid(), ["DeNoise.Viewer"], [GroupA]), returnTo);
        outcome.SessionToken.Should().NotBeNull();
        outcome.Location.Should().Be("/queue");
    }

    [Fact]
    public async Task Scenario_UserLacksResourceAccess_rerun_under_OIDC_is_unchanged()
    {
        var inA = await _api.OpenEpisodeAsync("oidc-a-1");
        var inB = await _api.OpenEpisodeAsync("oidc-b-1", _api.IntegrationB);
        using var client = await SessionAsync(await _browser.SignInAsync(OidcBrowser.Claims(NewOid(), ["DeNoise.Operator"], [GroupB])));

        using (var detail = await client.RawAsync(HttpMethod.Get, $"/api/v1/episodes/{inA}"))
        {
            detail.StatusCode.Should().Be(HttpStatusCode.NotFound, "existence must not leak");
        }
        using (var ack = await client.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{inA}/ack", new AckRequest(), ifMatch: 1))
        {
            ack.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        var page = await client.GetAsync<PagedResponse<EpisodeListItem>>("/api/v1/episodes?includeTotal=true");
        page!.Items.Select(i => i.Id).Should().Equal(inB);
    }

    [Fact]
    public async Task Federated_credential_redeems_the_code_with_a_client_assertion_and_no_secret()
    {
        var recorder = new RecordingHandler();
        await using var federated = await CreateApiAsync(credential: OidcCredentials.Federated, configure: services =>
        {
            services.RemoveAll<IClientAssertionProvider>();
            services.AddSingleton<IClientAssertionProvider>(new FakeAssertion());
            services.AddHttpClient(OidcIdentityProvider.HttpClientName).AddHttpMessageHandler(() => recorder);
        });
        using var browser = new OidcBrowser(federated);

        var outcome = await browser.SignInAsync(OidcBrowser.Claims(NewOid(), ["DeNoise.Viewer"], [GroupA]));

        outcome.SessionToken.Should().NotBeNull();
        var tokenRequest = recorder.Bodies.Single(b => b.Contains("grant_type=authorization_code", StringComparison.Ordinal));
        tokenRequest.Should().Contain("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer")
            .And.Contain("client_assertion=" + FakeAssertion.Value).And.Contain("code_verifier=").And.NotContain("client_secret");
    }

    [Fact]
    public async Task Providers_reports_the_sso_button_and_start_redirects_with_pkce_state_and_nonce()
    {
        using var client = _api.Client();
        var providers = await client.GetAsync<ProvidersResponse>("/auth/providers");
        providers!.Local.Should().BeTrue();
        providers.Oidc.Should().Be(new OidcProvider("Microsoft", "/auth/oidc/start"));

        var (url, _) = await _browser.StartAsync("/queue");
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        url.GetLeftPart(UriPartial.Path).Should().Be(mock.Authority() + "/authorize");
        q["client_id"].Should().Be(MockOidcServer.ClientId);
        q["response_type"].Should().Be("code");
        q["code_challenge_method"].Should().Be("S256");
        q["code_challenge"].Should().HaveLength(43);
        q["state"].Should().NotBeNullOrEmpty();
        q["nonce"].Should().NotBeNullOrEmpty().And.NotBe(q["state"]);
        q["redirect_uri"].Should().Be("http://localhost:5173/auth/oidc/callback");
    }

    [Fact]
    public async Task Without_Auth_Oidc_the_providers_response_is_unchanged_and_the_oidc_endpoints_are_404()
    {
        await using var plain = await ApiFixture.CreateAsync(postgres, "nooidc");
        using var client = plain.Client();
        using var providers = await client.RawAsync(HttpMethod.Get, "/auth/providers");
        (await providers.Content.ReadAsStringAsync()).Should().Be("""{"local":true,"oidc":null,"selfServiceReset":false}""");
        using (var start = await client.RawAsync(HttpMethod.Get, "/auth/oidc/start?returnTo=/queue"))
        {
            start.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        using var callback = await client.RawAsync(HttpMethod.Get, "/auth/oidc/callback?code=x&state=y");
        callback.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static string TraceId(HttpResponseMessage response) => response.Headers.GetValues("X-Trace-Id").Single();

    private sealed class FakeAssertion : IClientAssertionProvider
    {
        // The mock provider parses private_key_jwt assertions and requires sub == client_id; Entra instead checks the federated credential.
        public static readonly string Value = string.Join('.',
            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"alg":"RS256","typ":"JWT"}"""),
            Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode($$"""{"iss":"{{MockOidcServer.ClientId}}","sub":"{{MockOidcServer.ClientId}}","aud":"api://AzureADTokenExchange","exp":4102444800,"jti":"federated"}"""),
            "c2lnbmF0dXJl");

        public Task<string> GetAssertionAsync(CancellationToken ct = default) => Task.FromResult(Value);
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
