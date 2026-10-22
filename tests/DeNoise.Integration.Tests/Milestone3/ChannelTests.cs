using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DeNoise.Application.Notifications;
using DeNoise.Domain.Notifications;
using DeNoise.Domain.Ops;
using DeNoise.Infrastructure.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DeNoise.Integration.Tests.Milestone3;

/// <summary>Real HTTP and SMTP against in-process receivers: signature recipe, header contract, response classification, e-mail threading.</summary>
[Trait("Category", "Integration")]
public sealed class ChannelTests : IAsyncLifetime
{
    private WebApplication _receiver = null!;
    private string _baseUrl = string.Empty;
    private readonly ConcurrentQueue<(string Path, Dictionary<string, string> Headers, string Body)> _requests = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _receiver = builder.Build();
        _receiver.MapPost("/ok", async ctx => { await Capture(ctx); ctx.Response.StatusCode = 200; await ctx.Response.WriteAsync("""{"received":true}"""); });
        _receiver.MapPost("/gone", async ctx => { await Capture(ctx); ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("no such hook"); });
        _receiver.MapPost("/busy", async ctx => { await Capture(ctx); ctx.Response.StatusCode = 503; ctx.Response.Headers.RetryAfter = "120"; await ctx.Response.WriteAsync("try later"); });
        _receiver.MapPost("/slow", async ctx => { await Capture(ctx); await Task.Delay(3000, ctx.RequestAborted); ctx.Response.StatusCode = 200; });
        _receiver.MapPost("/redirect", async ctx => { await Capture(ctx); ctx.Response.Redirect("/ok"); });
        await _receiver.StartAsync();
        _baseUrl = _receiver.Urls.First();
    }

    public async Task DisposeAsync() => await _receiver.DisposeAsync();

    private async Task Capture(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var headers = ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue((ctx.Request.Path, headers, await reader.ReadToEndAsync()));
    }

    private WebhookChannel Channel(bool allowInsecure = true)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(WebhookChannel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        var provider = services.BuildServiceProvider();
        var options = Options.Create(new NotificationOptions { AllowInsecureDestinations = allowInsecure });
        return new WebhookChannel(provider.GetRequiredService<IHttpClientFactory>(), options, _time, NullLogger<WebhookChannel>.Instance);
    }

    private ResolvedDestination Destination(string path, string? secret = "s3cret", TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        var d = new Destination { DestinationId = Guid.NewGuid(), Name = "d", ChannelType = ChannelTypes.Webhook, FallbackDestinationId = Guid.NewGuid(), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        return new ResolvedDestination(d, _baseUrl + path, headers ?? new Dictionary<string, string>(), secret);
    }

    private static OutboxMessage Message(string type = NotificationTypes.EpisodeOpened) => new()
    {
        OutboxId = Guid.NewGuid(),
        EpisodeId = Guid.NewGuid(),
        Type = type,
        DestinationId = Guid.NewGuid(),
        Payload = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
        NotBefore = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Webhook_sends_the_contract_headers_and_a_verifiable_signature()
    {
        var message = Message();
        var body = """{"event":"episode.opened","episode":{"summary":"5xx \"},\"severity\":\"low"}}""";
        var result = await Channel().SendAsync(Destination("/ok", headers: new Dictionary<string, string> { ["X-Custom"] = "yes" }), message, body, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryOutcomes.Success);
        result.HttpStatus.Should().Be(200);
        result.ResponseExcerpt.Should().Be("""{"received":true}""");

        _requests.TryDequeue(out var req).Should().BeTrue();
        req.Body.Should().Be(body);
        req.Headers["X-DeNoise-Delivery-Id"].Should().Be(message.OutboxId.ToString());
        req.Headers["X-DeNoise-Event"].Should().Be(NotificationTypes.EpisodeOpened);
        req.Headers["X-Custom"].Should().Be("yes");
        req.Headers["User-Agent"].Should().StartWith("DeNoise/");
        req.Headers["Content-Type"].Should().StartWith("application/json");
        var timestamp = req.Headers["X-DeNoise-Timestamp"];
        timestamp.Should().Be(_time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        var expected = "v1=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(timestamp + "." + req.Body)));
        req.Headers["X-DeNoise-Signature"].Should().Be(expected, "receiver-side recompute over timestamp.body matches");
        ("v1=" + WebhookChannel.Sign("s3cret", timestamp, req.Body + "tampered")).Should().NotBe(expected, "a tampered body fails verification");
    }

    [Fact]
    public async Task Response_classification_permanent_retryable_with_retry_after_and_timeout_as_response_lost()
    {
        var channel = Channel();
        var gone = await channel.SendAsync(Destination("/gone"), Message(), "{}", CancellationToken.None);
        gone.Outcome.Should().Be(DeliveryOutcomes.Permanent);
        gone.HttpStatus.Should().Be(404);
        gone.ResponseExcerpt.Should().Be("no such hook");

        var busy = await channel.SendAsync(Destination("/busy"), Message(), "{}", CancellationToken.None);
        busy.Outcome.Should().Be(DeliveryOutcomes.Retryable);
        busy.HttpStatus.Should().Be(503);
        busy.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));

        var slow = await channel.SendAsync(Destination("/slow", timeout: TimeSpan.FromMilliseconds(300)), Message(), "{}", CancellationToken.None);
        slow.Outcome.Should().Be(DeliveryOutcomes.ResponseLost);

        var redirect = await channel.SendAsync(Destination("/redirect"), Message(), "{}", CancellationToken.None);
        redirect.Outcome.Should().Be(DeliveryOutcomes.Permanent, "redirects are not followed and 3xx is not success");

        var insecure = await Channel(allowInsecure: false).SendAsync(Destination("/ok"), Message(), "{}", CancellationToken.None);
        insecure.Outcome.Should().Be(DeliveryOutcomes.Permanent);
        insecure.Error.Should().Contain("https");

        var unreachable = await channel.SendAsync(new ResolvedDestination(Destination("/ok").Destination, "http://127.0.0.1:1/x", new Dictionary<string, string>(), null), Message(), "{}", CancellationToken.None);
        unreachable.Outcome.Should().Be(DeliveryOutcomes.Retryable);
    }

    [Fact]
    public async Task Smtp_channel_sends_a_threaded_plain_text_mail_with_a_stable_message_id()
    {
        using var sink = new SmtpSink();
        var options = Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = sink.Port, Security = "None", From = "hub@example.test", MessageIdDomain = "hub.test" });
        var channel = new SmtpEmailChannel(options, NullLogger<SmtpEmailChannel>.Instance);
        var destination = new Destination { DestinationId = Guid.NewGuid(), Name = "mail", ChannelType = ChannelTypes.SmtpEmail, FallbackDestinationId = Guid.NewGuid(), EmailTo = ["ops@example.test"], Timeout = TimeSpan.FromSeconds(10) };
        var episodeId = Guid.NewGuid();
        var message = new OutboxMessage { OutboxId = Guid.NewGuid(), EpisodeId = episodeId, Type = NotificationTypes.EpisodeClosed, DestinationId = destination.DestinationId, Payload = "{}", CreatedAt = DateTimeOffset.UtcNow, NotBefore = DateTimeOffset.UtcNow };
        var body = """{"event":"episode.closed","episode":{"severity":"critical","summary":"Disk <b>full</b>","resource":{"name":"db-1"},"conditionState":"resolved","handlingState":"closed","url":"https://hub/episodes/1","closure":{"reason":"source_resolved","evidence":"source"}}}""";

        var result = await channel.SendAsync(new ResolvedDestination(destination, null, new Dictionary<string, string>(), null), message, body, CancellationToken.None);

        result.Outcome.Should().Be(DeliveryOutcomes.Success);
        var mail = await sink.ReceivedAsync(TimeSpan.FromSeconds(10));
        mail.Should().Contain("Subject: [DeNoise][critical] Disk <b>full</b>").And.Contain("db-1");
        mail.Should().ContainEquivalentOf($"Message-Id: <{message.OutboxId:N}@hub.test>");
        mail.Should().ContainEquivalentOf($"References: <{episodeId:N}@hub.test>", "updates thread under the episode");
        mail.Should().Contain("Content-Type: text/plain").And.NotContain("text/html");
        mail.Should().Contain("Closure: source_resolved (evidence: source)");
        mail.Should().Contain("RCPT TO:<ops@example.test>");
    }

    /// <summary>Minimal SMTP server: accepts one message and records the conversation; enough for MailKit with no TLS.</summary>
    private sealed class SmtpSink : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<string> _received = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SmtpSink()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(ServeAsync);
        }

        public int Port { get; }

        public async Task<string> ReceivedAsync(TimeSpan timeout) => await _received.Task.WaitAsync(timeout);

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                var log = new StringBuilder();
                await writer.WriteLineAsync("220 sink ESMTP");
                string? line;
                var inData = false;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    log.AppendLine(line);
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            await writer.WriteLineAsync("250 OK queued");
                        }
                        continue;
                    }
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO": await writer.WriteLineAsync("250-sink"); await writer.WriteLineAsync("250 8BITMIME"); break;
                        case "HELO": await writer.WriteLineAsync("250 sink"); break;
                        case "MAIL": case "RCPT": case "NOOP": case "RSET": await writer.WriteLineAsync("250 OK"); break;
                        case "DATA": inData = true; await writer.WriteLineAsync("354 go ahead"); break;
                        case "QUIT": await writer.WriteLineAsync("221 bye"); _received.TrySetResult(log.ToString()); return;
                        default: await writer.WriteLineAsync("502 not implemented"); break;
                    }
                }
                _received.TrySetResult(log.ToString());
            }
            catch (Exception ex)
            {
                _received.TrySetException(ex);
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
