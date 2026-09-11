using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MT.Uptime.Core.Domain;
using MT.Uptime.Core.Monitoring;
using MT.Uptime.Core.Notifications;
using MT.Uptime.Core.Settings;

namespace MT.Uptime.Tests;

/// <summary>
/// What the journal says after an alert email, asserted against the real channel.
/// <para>
/// These exist because of a gap found by sending an alert through production on 2026-09-11: Slack left a
/// trace and email left none, so "did the alert email go out?" — the first question anyone asks after an
/// incident — could not be answered from the box. The cause was not the logging but the wiring. The SDK
/// builds its own <c>HttpClient</c> unless handed one, and that client never passes through
/// <see cref="RedactingHttpClientLogger"/>, so the email request was invisible while every other channel
/// was visible.
/// </para>
/// <para>
/// Injecting the client fixed that and made this file possible at all — the success path could not be
/// reached before without a network call, which is why it had no tests and why the defect survived.
/// </para>
/// </summary>
public class AlertEmailTests
{
    private const string ApiKey = "SG.tHiSiSaFaKeKeY.dOnOtLoGmE";
    private const string Recipient = "oncall@example.test";

    private static string Config() => JsonSerializer.Serialize(new EmailSettings
    {
        ApiKey = ApiKey,
        FromEmail = "alerts@example.test",
        FromName = "MT-Uptime",
        ToEmail = Recipient,
    });

    private static NotificationEvent Event() => new(
        MonitorId: 1,
        MonitorName: "api.example.com",
        NewStatus: MonitorStatus.Down,
        OldStatus: MonitorStatus.Up,
        At: new DateTime(2026, 9, 11, 19, 22, 0, DateTimeKind.Utc),
        Message: "Unexpected status 404",
        ResponseTimeMs: 237,
        Kind: NotifyKind.Down);

    /// <summary>
    /// The point of the refactor: the channel must use the client it is given. If the SDK were still
    /// building its own, this handler would never be called and the assertion below would fail — which
    /// is exactly the state the production gap was in.
    /// </summary>
    [Fact]
    public async Task The_injected_client_is_the_one_that_sends()
    {
        var (channel, handler, _) = Channel(HttpStatusCode.Accepted);

        var ok = await channel.SendAsync(Event(), Config(), CancellationToken.None);

        Assert.True(ok);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("api.sendgrid.com", handler.LastUri!.Host);
    }

    /// <summary>A 202 is SendGrid accepting the message for delivery, and is the normal success.</summary>
    [Fact]
    public async Task A_successful_send_says_so_and_names_the_monitor()
    {
        var (channel, _, sink) = Channel(HttpStatusCode.Accepted);

        await channel.SendAsync(Event(), Config(), CancellationToken.None);

        var line = Assert.Single(sink, l => l.Contains("accepted by SendGrid"));
        Assert.Contains("api.example.com", line);   // which alert this was, the thing the transport log cannot say
        Assert.Contains("202", line);
    }

    /// <summary>
    /// The two things that must never reach the journal. The recipient because on the hosted runtime it
    /// is a paying customer's address and this journal is shared with six other services; the API key
    /// because it is the credential, and it travels in a header rather than the URL — so the redaction
    /// in <see cref="RedactingHttpClientLogger"/> is not what keeps it out, and nothing else would.
    /// </summary>
    [Fact]
    public async Task Neither_the_recipient_nor_the_credential_is_logged()
    {
        var (channel, _, sink) = Channel(HttpStatusCode.Accepted);

        await channel.SendAsync(Event(), Config(), CancellationToken.None);

        Assert.NotEmpty(sink);
        Assert.DoesNotContain(sink, l => l.Contains(Recipient));
        Assert.DoesNotContain(sink, l => l.Contains(ApiKey));
        Assert.DoesNotContain(sink, l => l.Contains("SG."));
    }

    /// <summary>A rejection must be loud and must not be reported as a send.</summary>
    [Fact]
    public async Task A_rejected_send_reports_failure_and_does_not_claim_success()
    {
        var (channel, _, sink) = Channel(HttpStatusCode.Forbidden);

        var ok = await channel.SendAsync(Event(), Config(), CancellationToken.None);

        Assert.False(ok);
        Assert.DoesNotContain(sink, l => l.Contains("accepted by SendGrid"));
        Assert.Contains(sink, l => l.Contains("SendGrid send failed"));
    }

    [Fact]
    public async Task An_unconfigured_channel_sends_nothing_at_all()
    {
        var (channel, handler, sink) = Channel(HttpStatusCode.Accepted);

        var ok = await channel.SendAsync(Event(), JsonSerializer.Serialize(new EmailSettings()), CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(0, handler.Calls);   // no credential-less request goes out
        Assert.Contains(sink, l => l.Contains("not fully configured"));
    }

    // --- plumbing -------------------------------------------------------------------------------

    private static (SendGridNotificationChannel, StubHandler, List<string>) Channel(HttpStatusCode status)
    {
        var handler = new StubHandler(status);
        var sink = new List<string>();
        var channel = new SendGridNotificationChannel(
            new StubHttpClientFactory(handler), new CapturingLogger<SendGridNotificationChannel>(sink));
        return (channel, handler, sink);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
            => sink.Add(formatter(state, ex) + (ex is null ? "" : " " + ex));
    }
}
