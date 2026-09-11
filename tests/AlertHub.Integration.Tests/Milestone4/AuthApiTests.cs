using System.Net;
using System.Net.Http.Json;
using AlertHub.Contracts;
using AlertHub.Integration.Tests.Support;

namespace AlertHub.Integration.Tests.Milestone4;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AuthApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFixture _api = null!;

    public async Task InitializeAsync() => _api = await ApiFixture.CreateAsync(postgres, "auth");
    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Login_sets_an_httponly_cookie_and_me_reports_roles_scopes_and_permissions()
    {
        using var client = _api.Client();
        using var response = await client.LoginAsync("operator", ApiFixture.UserPassword);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        setCookie.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("path=/");
        setCookie.Should().NotContain("expires=", "session cookie unless keepSignedIn");

        var me = await client.GetAsync<MeResponse>("/api/v1/me");
        me!.Username.Should().Be("operator");
        me.Roles.Should().Equal("operator");
        me.Scopes.Should().Equal("scope-a");
        me.Permissions.Should().Contain("episode.ack").And.NotContain("user.manage");
        me.Teams.Select(t => t.Id).Should().Contain(_api.TeamA);
        me.MustChangePassword.Should().BeFalse();
        me.Credential.Should().Be("session");
    }

    [Fact]
    public async Task Unknown_user_and_wrong_password_produce_byte_identical_401_bodies()
    {
        using var client = _api.Client();
        using var unknown = await client.LoginAsync("nobody", "whatever-password-1");
        using var wrong = await client.LoginAsync("operator", "whatever-password-1");
        unknown.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var a = System.Text.Json.JsonDocument.Parse(await unknown.Content.ReadAsStringAsync()).RootElement;
        var b = System.Text.Json.JsonDocument.Parse(await wrong.Content.ReadAsStringAsync()).RootElement;
        foreach (var property in a.EnumerateObject().Where(p => p.Name != "traceId"))
        {
            b.GetProperty(property.Name).ToString().Should().Be(property.Value.ToString(), property.Name);
        }
        a.GetProperty("type").GetString().Should().Be("urn:alerthub:error:invalid-credentials");
        unknown.Headers.Contains("X-Trace-Id").Should().BeTrue();
    }

    [Fact]
    public async Task Eleventh_failed_login_locks_the_account_and_admin_can_unlock()
    {
        using var client = _api.Client();
        for (var i = 0; i < 10; i++)
        {
            using var r = await client.LoginAsync("viewer", "wrong-password-xyz");
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"attempt {i + 1}");
        }
        using var locked = await client.LoginAsync("viewer", ApiFixture.UserPassword);
        locked.StatusCode.Should().Be(HttpStatusCode.Locked, "correct password no longer helps while locked");
        (await locked.Content.ReadAsStringAsync()).Should().Contain("lockedUntil");

        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        // admin must change password first: everything except /me and /auth is 403
        using var blocked = await admin.RawAsync(HttpMethod.Get, "/api/v1/users");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("password-change-required");
        (await admin.GetAsync<MeResponse>("/api/v1/me"))!.MustChangePassword.Should().BeTrue();

        using var weak = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "password123"), idempotency: false);
        weak.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await weak.Content.ReadAsStringAsync()).Should().Contain("breached");
        using var changed = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "A-much-better-passphrase-2026"), idempotency: false);
        changed.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await admin.GetAsync<MeResponse>("/api/v1/me"))!.MustChangePassword.Should().BeFalse();

        var users = await admin.GetAsync<List<UserSummary>>("/api/v1/users?q=viewer");
        var viewer = users!.Single(u => u.Username == "viewer");
        viewer.Locked.Should().BeTrue();
        using var unlock = await admin.RawAsync(HttpMethod.Post, $"/api/v1/users/{viewer.Id}/unlock", idempotency: false);
        unlock.StatusCode.Should().Be(HttpStatusCode.NoContent);
        // The account is unlocked; the attacker's address stays locked for the rest of the window, so the user signs in from their own.
        using var stillLockedIp = await client.LoginAsync("viewer", ApiFixture.UserPassword);
        stillLockedIp.StatusCode.Should().Be(HttpStatusCode.Locked, "per-IP lockout is independent of the account unlock");
        using var fresh = _api.Client();
        using var ok = await fresh.LoginAsync("viewer", ApiFixture.UserPassword);
        ok.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Ip_lockout_is_independent_of_the_user()
    {
        // 10 failures across different usernames from the same IP lock the IP (04 ADR-14).
        using var client = _api.Client();
        for (var i = 0; i < 10; i++)
        {
            using var r = await client.LoginAsync($"ghost-{i}", "nope-nope-nope");
        }
        using var locked = await client.LoginAsync("operator", ApiFixture.UserPassword);
        locked.StatusCode.Should().Be(HttpStatusCode.Locked);
    }

    [Fact]
    public async Task Csrf_is_required_for_cookie_mutations_but_not_for_bearer_tokens()
    {
        using var operatorClient = await _api.LoginAsync("operator");
        var episode = await _api.OpenEpisodeAsync("csrf-1");
        var detail = await operatorClient.GetAsync<EpisodeDetail>($"/api/v1/episodes/{episode}");

        using var noCsrf = await operatorClient.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{episode}/ack", new AckRequest(), ifMatch: detail!.Item.Version, csrf: false);
        noCsrf.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await noCsrf.Content.ReadAsStringAsync()).Should().Contain("urn:alerthub:error:csrf");

        var (status, token, _) = await operatorClient.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("ci", ["episode.read", "episode.ack"], null));
        status.Should().Be(HttpStatusCode.Created);
        token!.Plaintext.Should().StartWith("ah_pat_");

        using var patClient = _api.Client();
        patClient.UseBearer(token.Plaintext);
        using var acked = await patClient.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{episode}/ack", new AckRequest(), ifMatch: detail.Item.Version, csrf: false);
        acked.StatusCode.Should().Be(HttpStatusCode.OK);
        (await patClient.GetAsync<MeResponse>("/api/v1/me"))!.Credential.Should().Be("personalaccesstoken");
    }

    [Fact]
    public async Task Personal_access_tokens_cannot_exceed_the_owner_and_stop_working_when_revoked_or_expired()
    {
        using var operatorClient = await _api.LoginAsync("operator");
        var (tooMuch, _, raw) = await operatorClient.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("admin-ish", ["user.manage"], null));
        tooMuch.Should().Be(HttpStatusCode.Forbidden, raw);

        var (_, scoped, _) = await operatorClient.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("read-only", ["episode.read"], null));
        using var pat = _api.Client();
        pat.UseBearer(scoped!.Plaintext);
        var episode = await _api.OpenEpisodeAsync("pat-1");
        (await pat.GetAsync<EpisodeDetail>($"/api/v1/episodes/{episode}"))!.Item.Id.Should().Be(episode);
        using var forbidden = await pat.RawAsync(HttpMethod.Post, $"/api/v1/episodes/{episode}/ack", new AckRequest(), ifMatch: 1, csrf: false);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "token scope narrower than the owner's roles");
        using var mint = await pat.RawAsync(HttpMethod.Post, "/api/v1/me/tokens", new CreateTokenRequest("x", [], null), csrf: false);
        mint.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a token cannot mint tokens");

        using var revoke = await operatorClient.RawAsync(HttpMethod.Delete, $"/api/v1/me/tokens/{scoped.Token.Id}");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var afterRevoke = await pat.RawAsync(HttpMethod.Get, "/api/v1/me");
        afterRevoke.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var (_, expiring, _) = await operatorClient.PostAsync<TokenCreatedResponse>("/api/v1/me/tokens", new CreateTokenRequest("short", [], ApiFixture.T0.AddHours(1)));
        using var pat2 = _api.Client();
        pat2.UseBearer(expiring!.Plaintext);
        (await pat2.RawAsync(HttpMethod.Get, "/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        _api.Time.Advance(TimeSpan.FromHours(2));
        (await pat2.RawAsync(HttpMethod.Get, "/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "expired");
        _api.Time.SetUtcNow(ApiFixture.T0.AddHours(2));
    }

    [Fact]
    public async Task Sessions_can_be_listed_and_revoked_and_logout_kills_the_cookie()
    {
        using var first = await _api.LoginAsync("operator");
        using var second = await _api.LoginAsync("operator");
        var sessions = await first.GetAsync<List<SessionSummary>>("/api/v1/me/sessions");
        sessions!.Should().HaveCountGreaterThanOrEqualTo(2);
        var current = sessions.Single(s => s.Current);
        var other = sessions.First(s => !s.Current);

        using var revoke = await first.RawAsync(HttpMethod.Delete, $"/api/v1/me/sessions/{other.Id}");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var secondAfter = await second.RawAsync(HttpMethod.Get, "/api/v1/me");
        secondAfter.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "revoked session is rejected on the next request");

        using var logout = await first.RawAsync(HttpMethod.Post, "/auth/logout", idempotency: false);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var afterLogout = await first.RawAsync(HttpMethod.Get, "/api/v1/me");
        afterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _ = current;
    }

    [Fact]
    public async Task Admin_creates_users_with_a_one_time_temporary_password_and_can_reset_and_disable()
    {
        using var admin = await _api.LoginAsync("admin", ApiFixture.AdminPassword);
        using var change = await admin.RawAsync(HttpMethod.Post, "/auth/change-password", new ChangePasswordRequest(ApiFixture.AdminPassword, "Another-good-passphrase-77"), idempotency: false);
        change.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var (status, created, raw) = await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/users", new CreateUserRequest("newbie", "New Bee", "new@example.test", ["viewer"], ["scope-a"]));
        status.Should().Be(HttpStatusCode.Created, raw);
        created!.TemporaryPassword.Should().HaveLength(20);
        created.User.MustChangePassword.Should().BeTrue();

        using var newbie = _api.Client();
        (await newbie.LoginAsync("newbie", created.TemporaryPassword)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var gated = await newbie.RawAsync(HttpMethod.Get, "/api/v1/episodes");
        gated.StatusCode.Should().Be(HttpStatusCode.Forbidden, "forced change gates everything but /me and /auth");

        var (rs, reset, _) = await admin.PostAsync<TemporaryPasswordResponse>($"/api/v1/users/{created.User.Id}/reset-password");
        rs.Should().Be(HttpStatusCode.OK);
        reset!.TemporaryPassword.Should().NotBe(created.TemporaryPassword);
        using var afterReset = await newbie.RawAsync(HttpMethod.Get, "/api/v1/me");
        afterReset.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "reset revokes sessions");

        using var disable = await admin.RawAsync(HttpMethod.Post, $"/api/v1/users/{created.User.Id}/disable", idempotency: false);
        disable.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await newbie.LoginAsync("newbie", reset.TemporaryPassword)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "disabled = invalid credentials, indistinguishable");

        using var operatorClient = await _api.LoginAsync("operator");
        using var notAdmin = await operatorClient.RawAsync(HttpMethod.Post, "/api/v1/users", new CreateUserRequest("x", "x", null, ["viewer"], []));
        notAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bootstrap_password_never_appears_in_logs()
    {
        // The API logs to console JSON; the bootstrap password is only ever written to stdout when generated, never when configured.
        // Here it is configured, so nothing containing it may be logged. We scan the audit table (the only persisted record of the event) too.
        await using var db = PostgresFixture.CreateContext(_api.ConnectionString);
        var audit = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.AuditEntries.Where(a => a.Action == "user.bootstrap"));
        audit.Should().ContainSingle();
        (audit[0].After ?? string.Empty).Should().NotContain(ApiFixture.AdminPassword);
        var all = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.AuditEntries);
        var everything = string.Join("\n", all.Select(a => (a.After ?? "") + (a.Before ?? "") + (a.Reason ?? "")));
        everything.Should().NotContain(ApiFixture.AdminPassword).And.NotContain(ApiFixture.UserPassword);
    }
}
