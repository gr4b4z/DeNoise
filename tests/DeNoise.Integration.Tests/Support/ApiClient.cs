using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DeNoise.Application.Abstractions;
using DeNoise.Contracts;

namespace DeNoise.Integration.Tests.Support;

/// <summary>Thin client over the API host with cookie/CSRF/Idempotency-Key plumbing, so tests read like the contract.</summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _cookie;
    private string? _csrf;
    private string? _bearer;

    private static int _clients;

    /// <summary>Each client gets its own synthetic address so per-IP lockout tests do not bleed into one another.</summary>
    public ApiClient(HttpClient http)
    {
        _http = http;
        var n = Interlocked.Increment(ref _clients);
        _http.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}");
    }

    public static readonly JsonSerializerOptions Json = JsonDefaults.Stored;

    public string? Cookie => _cookie;

    public void UseBearer(string token)
    {
        _bearer = token;
        _cookie = null;
        _csrf = null;
    }

    public async Task<HttpResponseMessage> LoginAsync(string username, string password)
    {
        var response = await SendAsync(HttpMethod.Post, "/auth/login", new LoginRequest(username, password), csrf: false, idempotency: false);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            var setCookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("denoise_session=", StringComparison.Ordinal));
            _cookie = setCookie.Split(';')[0]["denoise_session=".Length..];
            _bearer = null;
            var csrf = await GetAsync<CsrfResponse>("/auth/csrf");
            _csrf = csrf!.Token;
        }
        return response;
    }

    public Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body = null, int? ifMatch = null, Guid? idempotencyKey = null, bool csrf = true, bool idempotency = true)
        => SendAsync(method, path, body, ifMatch, idempotencyKey, csrf, idempotency);

    public async Task<T?> GetAsync<T>(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, csrf: false, idempotency: false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json);
    }

    public async Task<(HttpStatusCode Status, T? Body, string Raw)> PostAsync<T>(string path, object? body = null, int? ifMatch = null, Guid? idempotencyKey = null)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ifMatch, idempotencyKey, csrf: true, idempotency: true);
        var raw = await response.Content.ReadAsStringAsync();
        T? parsed = default;
        if (response.IsSuccessStatusCode && raw.Length > 0) parsed = JsonSerializer.Deserialize<T>(raw, Json);
        return (response.StatusCode, parsed, raw);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, int? ifMatch = null, Guid? idempotencyKey = null, bool csrf = true, bool idempotency = true)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
        if (_bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearer);
        else if (_cookie is not null) request.Headers.Add("Cookie", $"denoise_session={_cookie}");
        if (csrf && _csrf is not null && method != HttpMethod.Get) request.Headers.Add("X-CSRF-Token", _csrf);
        if (ifMatch is { } v) request.Headers.TryAddWithoutValidation("If-Match", $"\"{v}\"");
        if (idempotency && method == HttpMethod.Post && !path.StartsWith("/auth", StringComparison.Ordinal)) request.Headers.Add("Idempotency-Key", (idempotencyKey ?? Guid.NewGuid()).ToString());
        return await _http.SendAsync(request);
    }

    public void Dispose() => _http.Dispose();
}
