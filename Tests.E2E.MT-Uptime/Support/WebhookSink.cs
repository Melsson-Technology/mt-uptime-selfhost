using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace MT.Uptime.Tests.E2E.Support;

/// <summary>
/// One alert as the product actually delivered it, parsed from the JSON
/// <c>WebhookNotificationChannel</c> POSTs.
/// <para>
/// Deliberately a record over the wire format rather than a re-use of any internal type. The payload
/// is a public contract — somebody's Zapier hook is parsing it — so a test that asserted against the
/// C# object would keep passing through a rename that broke every consumer. The property names here
/// are the JSON names, and that mapping is the thing under test.
/// </para>
/// </summary>
public sealed record WebhookAlert(
    int MonitorId,
    string Monitor,
    string Kind,
    string Status,
    string PreviousStatus,
    string? Message,
    double? ResponseTimeMs,
    DateTimeOffset Timestamp,
    JsonElement Raw)
{
    /// <summary>The nested <c>incident</c> object, or null when the alert carried none.</summary>
    public JsonElement? Incident => Nested("incident");

    /// <summary>The nested <c>diagnostics</c> object, or null when enrichment was absent.</summary>
    public JsonElement? Diagnostics => Nested("diagnostics");

    public int? IncidentId => Incident?.TryGetProperty("id", out var v) == true ? v.GetInt32() : null;
    public int? MonitorCount => Incident?.TryGetProperty("monitorCount", out var v) == true ? v.GetInt32() : null;
    public string? LastStatusCode =>
        Diagnostics?.TryGetProperty("lastStatusCode", out var v) == true && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private JsonElement? Nested(string name)
        => Raw.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public override string ToString() => $"{Kind} for #{MonitorId} '{Monitor}' ({PreviousStatus}→{Status}): {Message}";
}

/// <summary>
/// An HTTP endpoint the tests host, so a webhook notification channel has somewhere real to deliver
/// to and the assertion is about what arrived rather than about what was queued.
/// <para>
/// <b>HttpListener, not Kestrel.</b> The pipeline tier already hosts the whole application through
/// <see cref="E2EAppFactory"/>, whose in-memory transport binds no port at all — so the sink cannot
/// borrow it, and standing a second Kestrel up beside it means a second host, a second logger and a
/// second set of shutdown semantics for what is a queue behind a 200. HttpListener is in the BCL,
/// binds a real port that the real <c>HttpClient</c> inside the application can reach, and is about
/// forty lines.
/// </para>
/// <para>
/// <b>Bound to 127.0.0.1 on an ephemeral port.</b> Ephemeral because two test classes must never
/// contend for a literal; loopback because this is deliberately an unauthenticated endpoint that
/// accepts anything posted to it, and it should not be reachable from off the box.
/// </para>
/// </summary>
public sealed class WebhookSink : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentQueue<WebhookAlert> _received = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    public WebhookSink()
    {
        // Port 0 is not usable with HttpListener's prefix syntax, so a free port is found the ordinary
        // way — bind a socket to 0, read what the OS chose, release it — and there is a race in that
        // gap by construction. It is one process on a loopback interface reusing a port the kernel
        // just handed out; the alternative is a fixed port, which turns a rare race into a permanent
        // collision the first time two classes overlap.
        Port = FreePort();
        Url = $"http://127.0.0.1:{Port}/webhook/";

        _listener.Prefixes.Add(Url);
        _listener.Start();
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The URL to store on the notification channel. Ends with a slash: HttpListener prefixes must.</summary>
    public string Url { get; }

    public int Port { get; }

    /// <summary>Everything received so far, oldest first.</summary>
    public IReadOnlyList<WebhookAlert> Received => _received.ToArray();

    /// <summary>
    /// Waits for an alert matching <paramref name="monitorId"/> and <paramref name="kind"/>.
    /// <para>
    /// The default deadline is generous on purpose. Between the target breaking and this returning
    /// sit: up to one interval of startup jitter, the check itself, <c>RetryCount + 1</c> consecutive
    /// failures for a soft one, then the dispatcher. Polling at 200 ms across that is free; a fixed
    /// sleep tuned to the happy path is the classic way to make a suite that fails on a loaded box.
    /// </para>
    /// </summary>
    public async Task<WebhookAlert> WaitForAsync(
        int monitorId,
        string kind,
        TimeSpan? within = null,
        CancellationToken ct = default)
    {
        var timeout = within ?? TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var hit = Match(monitorId, kind);
            if (hit is not null) return hit;
            await Task.Delay(200, ct);
        }

        // The failure message carries everything that DID arrive. "Expected a Down webhook, got none"
        // is almost never the useful half — "got a Degraded instead" or "got a Down for the other
        // monitor" is, and without it the next step is always to add this logging by hand.
        var seen = _received.IsEmpty
            ? "nothing at all"
            : string.Join("\n  ", _received.Select(a => a.ToString()));

        throw new TimeoutException(
            $"No '{kind}' webhook for monitor {monitorId} within {timeout.TotalSeconds:0}s. Received:\n  {seen}");
    }

    /// <summary>
    /// Asserts that nothing matching arrives within <paramref name="window"/> — for maintenance
    /// suppression, and for "no alert during the Pending beats".
    /// <para>
    /// A negative like this is only worth as much as its window. Too short and it passes because the
    /// alert had not been sent yet, which is the same result as suppression working and means nothing.
    /// Callers should pass a window comfortably longer than the delivery they are ruling out.
    /// </para>
    /// </summary>
    public async Task AssertNoneAsync(
        int monitorId,
        string kind,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var hit = Match(monitorId, kind);
            if (hit is not null)
                throw new InvalidOperationException(
                    $"Expected no '{kind}' webhook for monitor {monitorId} within {window.TotalSeconds:0}s, "
                    + $"but one arrived: {hit}");
            await Task.Delay(200, ct);
        }
    }

    /// <summary>
    /// Waits for an alert about a monitor identified by <b>name</b>.
    /// <para>
    /// For the browser tier, which never learns a monitor's id: it creates monitors through a form and
    /// knows them the way an operator does. The id-based overload stays the primary one, because
    /// everywhere else the id is available and is unambiguous — two monitors may share a name.
    /// </para>
    /// </summary>
    public async Task<WebhookAlert> WaitForAsync(
        string monitorName,
        string kind,
        TimeSpan? within = null,
        CancellationToken ct = default)
    {
        var timeout = within ?? TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var hit = MatchByName(monitorName, kind);
            if (hit is not null) return hit;
            await Task.Delay(200, ct);
        }

        var seen = _received.IsEmpty
            ? "nothing at all"
            : string.Join("\n  ", _received.Select(a => a.ToString()));

        throw new TimeoutException(
            $"No '{kind}' webhook for monitor '{monitorName}' within {timeout.TotalSeconds:0}s. Received:\n  {seen}");
    }

    /// <summary>The name-keyed counterpart of <see cref="AssertNoneAsync(int, string, TimeSpan, CancellationToken)"/>.</summary>
    public async Task AssertNoneAsync(
        string monitorName,
        string kind,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + window;
        while (DateTime.UtcNow < deadline)
        {
            var hit = MatchByName(monitorName, kind);
            if (hit is not null)
                throw new InvalidOperationException(
                    $"Expected no '{kind}' webhook for monitor '{monitorName}' within {window.TotalSeconds:0}s, "
                    + $"but one arrived: {hit}");
            await Task.Delay(200, ct);
        }
    }

    /// <summary>Forgets everything received, so one test can reuse the sink across phases.</summary>
    public void Clear() => _received.Clear();

    private WebhookAlert? Match(int monitorId, string kind) =>
        _received.FirstOrDefault(a =>
            a.MonitorId == monitorId && string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));

    private WebhookAlert? MatchByName(string monitorName, string kind) =>
        _received.FirstOrDefault(a =>
            string.Equals(a.Monitor, monitorName, StringComparison.Ordinal)
            && string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));

    private async Task PumpAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;   // Stop() aborts the pending GetContextAsync; that is the shutdown path
            }
            catch (HttpListenerException)
            {
                return;   // the listener was disposed underneath us
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                if (TryParse(body, out var alert)) _received.Enqueue(alert);
            }
            catch (Exception e)
            {
                // Never let one malformed delivery kill the pump: the remaining tests would then all
                // time out waiting on a sink that stopped listening, and none of their messages would
                // mention this.
                Console.Error.WriteLine($"WebhookSink: could not read a delivery: {e.Message}");
            }
            finally
            {
                // 2xx always, including for a body we failed to parse. The channel reports success on
                // IsSuccessStatusCode, and answering 500 would make the product log a delivery failure
                // for what is the sink's problem — sending the investigation somewhere false.
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                try { context.Response.Close(); } catch { /* client already gone */ }
            }
        }
    }

    private static bool TryParse(string body, out WebhookAlert alert)
    {
        alert = default!;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            // Cloned because the JsonDocument is disposed at the end of this scope and the alert
            // outlives it — a JsonElement pointing into a returned buffer is a use-after-free with
            // extra steps, and it surfaces as intermittently empty fields rather than as a crash.
            var root = doc.RootElement.Clone();

            alert = new WebhookAlert(
                MonitorId: root.GetProperty("monitorId").GetInt32(),
                Monitor: root.GetProperty("monitor").GetString() ?? "",
                Kind: root.GetProperty("kind").GetString() ?? "",
                Status: root.GetProperty("status").GetString() ?? "",
                PreviousStatus: root.GetProperty("previousStatus").GetString() ?? "",
                Message: root.TryGetProperty("message", out var m) ? m.GetString() : null,
                ResponseTimeMs: root.TryGetProperty("responseTimeMs", out var r) && r.ValueKind == JsonValueKind.Number
                    ? r.GetDouble()
                    : null,
                Timestamp: root.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(t.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : DateTimeOffset.UtcNow,
                Raw: root);
            return true;
        }
        catch (Exception e)
        {
            // A payload that does not parse is a finding, not a shrug: the shape of this JSON is a
            // public contract. Recorded loudly and dropped, so the waiting test times out with its own
            // message rather than hanging on a queue this would have silently kept empty.
            Console.Error.WriteLine($"WebhookSink: delivery did not parse as an alert ({e.Message}): {body}");
            return false;
        }
    }

    private static int FreePort()
    {
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        try { _listener.Close(); } catch { /* already closed */ }
        // Bounded: the pump is blocked in GetContextAsync, which Stop() aborts, but a delivery being
        // read at that moment could still be in flight. Waiting forever here would hang the test run
        // on a detail that no longer matters once the listener is closed.
        try { _pump.Wait(TimeSpan.FromSeconds(5)); } catch { /* nothing useful to do at teardown */ }
        _stopping.Dispose();
    }
}
