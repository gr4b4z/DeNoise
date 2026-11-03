using System.Net;
using System.Text.Json;
using DeNoise.Integration.Tests.Milestone4;
using DeNoise.Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DeNoise.Integration.Tests.Milestone11b;

/// <summary>What the browser saw at the end of the callback: always a redirect; a session cookie only on success.</summary>
public sealed record SsoOutcome(HttpStatusCode Status, string Location, string? SessionToken, string Body, string CallbackQuery, string StateCookie);

/// <summary>
/// Plays the browser through the M11b flow without following redirects: <c>GET /auth/oidc/start</c> on the API, the
/// mock provider's login form (which takes the claims to put in the id_token), then <c>GET /auth/oidc/callback</c> with
/// the state cookie. Each step is exposed so scenarios can tamper with one of them.
/// </summary>
public sealed class OidcBrowser : IDisposable
{
    private static readonly HttpClient Idp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient _app;
    private static int _clients;

    public OidcBrowser(ApiFixture api)
    {
        _app = api.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        var n = Interlocked.Increment(ref _clients);
        _app.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.200.{(n >> 8) & 255}.{n & 255}");
    }

    public static Dictionary<string, object> Claims(string oid, string[] roles, string[]? groups = null, string? email = null, string? name = null, string? upn = null)
    {
        var claims = new Dictionary<string, object> { ["oid"] = oid, ["roles"] = roles, ["groups"] = groups ?? [] };
        if (email is not null) claims["email"] = email;
        if (name is not null) claims["name"] = name;
        if (upn is not null) claims["preferred_username"] = upn;
        return claims;
    }

    public async Task<(Uri AuthorizeUrl, string StateCookie)> StartAsync(string? returnTo = "/queue")
    {
        var path = "/auth/oidc/start" + (returnTo is null ? string.Empty : "?returnTo=" + Uri.EscapeDataString(returnTo));
        using var response = await _app.GetAsync(new Uri(path, UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "start redirects to the identity provider");
        var cookie = CookieValue(response, "denoise_oidc");
        cookie.Should().NotBeNullOrEmpty();
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("denoise_oidc=", StringComparison.Ordinal));
        setCookie.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("path=/auth/oidc");
        return (response.Headers.Location!, cookie!);
    }

    /// <summary>Submits the mock provider's login form; returns the callback query string (<c>?code=…&amp;state=…</c>).</summary>
    public static async Task<string> AuthorizeAsync(Uri authorizeUrl, IDictionary<string, object> claims, string username = "sso-user")
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = username, ["claims"] = JsonSerializer.Serialize(claims) });
        using var response = await Idp.PostAsync(authorizeUrl, form);
        response.StatusCode.Should().Be(HttpStatusCode.Found, "the mock provider redirects back with a code");
        return response.Headers.Location!.Query;
    }

    public async Task<SsoOutcome> CallbackAsync(string query, string? stateCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/auth/oidc/callback" + query, UriKind.Relative));
        if (stateCookie is not null) request.Headers.Add("Cookie", $"denoise_oidc={stateCookie}");
        using var response = await _app.SendAsync(request);
        var session = CookieValue(response, "denoise_session");
        return new SsoOutcome(response.StatusCode, response.Headers.Location?.ToString() ?? string.Empty, string.IsNullOrEmpty(session) ? null : session,
            await response.Content.ReadAsStringAsync(), query, stateCookie ?? string.Empty);
    }

    public async Task<SsoOutcome> SignInAsync(IDictionary<string, object> claims, string? returnTo = "/queue")
    {
        var (url, cookie) = await StartAsync(returnTo);
        var query = await AuthorizeAsync(url, claims);
        return await CallbackAsync(query, cookie);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _app.GetAsync(new Uri(path, UriKind.Relative));

    private static string? CookieValue(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return null;
        var header = values.FirstOrDefault(c => c.StartsWith(name + "=", StringComparison.Ordinal));
        return header?.Split(';')[0][(name.Length + 1)..];
    }

    public void Dispose() => _app.Dispose();
}
