using System.Net.Http.Headers;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Microsoft.AspNetCore.StaticFiles;
using ServerMap.Web;

using var runtimeLease = ServerHostRuntimeStager.AcquireCurrentLease();
var arguments = args.Select((value, index) => (value, index))
    .Where(item => item.value.StartsWith("--", StringComparison.Ordinal) && item.index + 1 < args.Length)
    .ToDictionary(item => item.value[2..], item => args[item.index + 1], StringComparer.OrdinalIgnoreCase);
if (!arguments.TryGetValue("config", out var configPath) || !File.Exists(configPath))
    return 2;
arguments.TryGetValue("state", out var statePath);

var settings = JsonSerializer.Deserialize<ServerMapSettings>(await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid ServerMap host configuration.");
using var hostLock = BackgroundHostFiles.AcquireHost(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
using var currentProcess = Process.GetCurrentProcess();
var state = new BackgroundHostState
{
    ProcessId = Environment.ProcessId,
    ProcessStartTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks,
    ExecutablePath = Environment.ProcessPath ?? "",
    ListenAddress = settings.ListenAddress,
    ListenPort = settings.ListenPort,
    Url = string.IsNullOrWhiteSpace(settings.PublicUrl)
        ? $"{(settings.UseHttps ? "https" : "http")}://{(settings.ListenAddress.Contains(':') ? "[" + settings.ListenAddress + "]" : settings.ListenAddress is "0.0.0.0" ? "127.0.0.1" : settings.ListenAddress)}:{settings.ListenPort}/"
        : settings.PublicUrl
};
async Task WriteStateAsync(bool running)
{
    state.IsRunning = running;
    state.HeartbeatUtc = DateTimeOffset.UtcNow;
    if (!string.IsNullOrWhiteSpace(statePath)) await BackgroundHostFiles.WriteAsync(statePath, state);
}
var profileRoot = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
string Resolve(string value, string fallback) => Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : Path.IsPathRooted(value) ? value : Path.Combine(profileRoot, value));

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
builder.WebHost.ConfigureKestrel(options =>
{
    var address = System.Net.IPAddress.Parse(settings.ListenAddress);
    options.Listen(address, settings.ListenPort, listen =>
    {
        if (!settings.UseHttps) return;
        var certificate = X509Certificate2.CreateFromPemFile(Resolve(settings.CertificatePath, string.Empty), Resolve(settings.PrivateKeyPath, string.Empty));
        listen.UseHttps(certificate);
    });
});
var app = builder.Build();
using var stopRequests = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
// Cancel upstream SSE, pending response headers, uploads and static downloads
// before Kestrel waits for requests to drain. Browser disconnect alone is insufficient.
app.Use(async (context, next) =>
{
    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopRequests.Token);
    context.RequestAborted = requestCancellation.Token;
    using var abortOnStop = stopRequests.Token.Register(context.Abort);
    try { await next(context); }
    catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested) { context.Abort(); }
    catch (IOException) when (requestCancellation.IsCancellationRequested) { context.Abort(); }
});
var defaultWebRoot = Path.Combine(AppContext.BaseDirectory, "WebRoot");
var webRoot = Resolve(settings.WebRoot, defaultWebRoot);
var contentTypes = new FileExtensionContentTypeProvider();
// Forward only each incoming browser's Cookie header. A shared CookieContainer
// would otherwise replay the last user's (possibly admin) session to guests.
var backend = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
{
    BaseAddress = new Uri($"http://127.0.0.1:{settings.BackendPort}/"),
    Timeout = Timeout.InfiniteTimeSpan
};

app.Map("/api/{**path}", async context =>
{
    var target = context.Request.Path + context.Request.QueryString;
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
    foreach (var header in context.Request.Headers)
        if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) && !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    if (!string.IsNullOrWhiteSpace(settings.BackendToken))
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BackendToken);
    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        request.Content = new StreamContent(context.Request.Body);
    using var response = await backend.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var header in response.Headers.Concat(response.Content.Headers))
        context.Response.Headers[header.Key] = header.Value.ToArray();
    context.Response.Headers.Remove("transfer-encoding");
    if (settings.UseHttps && context.Response.Headers.TryGetValue("Set-Cookie", out var cookies))
        context.Response.Headers["Set-Cookie"] = cookies.Select(cookie => cookie.Contains("Secure", StringComparison.OrdinalIgnoreCase) ? cookie : cookie + "; Secure").ToArray();
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
});

// MapFallback uses the framework's non-file constraint, which excludes the
// .js/.css/.png assets used by the map. Handle every non-API path explicitly.
app.Map("/{**path}", async context =>
{
    var relative = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
    if (string.IsNullOrWhiteSpace(relative)) relative = "index.html";
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(webRoot)) + Path.DirectorySeparatorChar;
    var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
    {
        context.Response.StatusCode = 404;
        return;
    }
    if (contentTypes.TryGetContentType(file, out var type)) context.Response.ContentType = type;
    if (relative.Equals("index.html", StringComparison.OrdinalIgnoreCase))
    {
        // Emit metadata in the initial HTML for crawlers as well as browsers.
        // Never let an offline game backend hold up the map shell indefinitely.
        var site = new WebPageMetadata();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/announcement");
            if (!string.IsNullOrWhiteSpace(settings.BackendToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.BackendToken);
            using var response = await backend.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (document.RootElement.TryGetProperty("site", out var value)) site = (value.Deserialize<WebPageMetadata>() ?? site).Normalize();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or ArgumentException) { }
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(site.ApplyToHtml(await File.ReadAllTextAsync(file, context.RequestAborted)), context.RequestAborted);
        return;
    }
    await context.Response.SendFileAsync(file, context.RequestAborted);
});

try
{
    var lifecycleClock = Stopwatch.StartNew();
    app.Logger.LogInformation("Map Host stage started: listen.");
    await app.StartAsync();
    await WriteStateAsync(true);
    app.Logger.LogInformation("Map Host stage completed: listen. ElapsedMs={ElapsedMs}.", lifecycleClock.ElapsedMilliseconds);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
    var heartbeatClock = Stopwatch.StartNew();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        if (arguments.TryGetValue("stop", out var stopPath) && File.Exists(stopPath)) break;
        try
        {
            if (heartbeatClock.Elapsed >= TimeSpan.FromSeconds(1))
            {
                await WriteStateAsync(true);
                heartbeatClock.Restart();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep serving during transient state-file locks; the next heartbeat retries.
        }
        try { await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping); }
        catch (OperationCanceledException) { break; }
    }
    lifecycleClock.Restart();
    app.Logger.LogInformation("Map Host stage started: cancel-active-requests.");
    await stopRequests.CancelAsync();
    app.Logger.LogInformation("Map Host stage completed: cancel-active-requests. ElapsedMs={ElapsedMs}.", lifecycleClock.ElapsedMilliseconds);
    lifecycleClock.Restart();
    app.Logger.LogInformation("Map Host stage started: stop-listener.");
    await app.StopAsync();
    app.Logger.LogInformation("Map Host stage completed: stop-listener. ElapsedMs={ElapsedMs}.", lifecycleClock.ElapsedMilliseconds);
    return 0;
}
catch (Exception ex)
{
    state.Error = ex.Message;
    return 1;
}
finally
{
    try { await WriteStateAsync(false); }
    finally
    {
        await stopRequests.CancelAsync();
        backend.Dispose();
        await app.DisposeAsync();
    }
}
