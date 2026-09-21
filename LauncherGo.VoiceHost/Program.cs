using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LauncherGo.Domains.Models;
using LauncherGo.Services;

using var lease = ServerHostRuntimeStager.AcquireCurrentLease();
var arguments = args.Select((value, index) => (value, index))
    .Where(x => x.value.StartsWith("--") && x.index + 1 < args.Length)
    .ToDictionary(x => x.value[2..], x => args[x.index + 1]);
if (!arguments.TryGetValue("config", out var configPath) || !arguments.TryGetValue("state", out var statePath)
    || !arguments.TryGetValue("stop", out var stopPath)) return 2;
var settings = JsonSerializer.Deserialize<VoiceSettings>(await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Invalid voice configuration.");
using var hostLock = BackgroundHostFiles.AcquireHost(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
using var process = Process.GetCurrentProcess();
var state = new BackgroundHostState
{
    ProcessId = process.Id, ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
    ExecutablePath = Environment.ProcessPath ?? "", ListenAddress = settings.ListenAddress, ListenPort = settings.ListenPort,
    Url = new VoiceWebService().GetUrl(settings)
};
async Task WriteState(bool running)
{
    state.IsRunning = running; state.HeartbeatUtc = DateTimeOffset.UtcNow;
    await BackgroundHostFiles.WriteAsync(statePath, state);
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(3));
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Parse(settings.ListenAddress), settings.ListenPort, listen =>
{
    if (settings.UseHttps) listen.UseHttps(X509Certificate2.CreateFromPemFile(settings.CertificatePath, settings.PrivateKeyPath));
}));
var app = builder.Build();
using var stopping = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
using var backendHttp = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
using var pageStream = typeof(Program).Assembly.GetManifestResourceStream("LauncherGo.Voice.index.html")!;
using var reader = new StreamReader(pageStream, Encoding.UTF8);
string page = await reader.ReadToEndAsync();
app.Use(async (context, next) =>
{
    using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping.Token);
    context.RequestAborted = cancel.Token;
    using var abort = stopping.Token.Register(context.Abort);
    try { await next(context); }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException) { context.Abort(); }
});
app.UseWebSockets();
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
app.MapGet("/index.html", () => Results.Content(page, "text/html; charset=utf-8"));
app.MapGet("/assets/{name}", (string name) =>
{
    var asset = typeof(Program).Assembly.GetManifestResourceStream("LauncherGo.Voice.assets." + name);
    if (asset == null) return Results.NotFound();
    string contentType = Path.GetExtension(name) switch
    {
        ".svg" => "image/svg+xml", ".jpg" => "image/jpeg", _ => "text/plain"
    };
    return Results.Stream(asset, contentType);
});
app.MapGet("/health", () => Results.Json(new { service = "LauncherGo.VoiceHost", protocol = 1 }));
app.MapGet("/backend-health", async (HttpContext context) =>
{
    bool connected = false;
    try
    {
        using var response = await backendHttp.GetAsync($"http://127.0.0.1:{settings.BackendPort}/health", context.RequestAborted);
        if (response.IsSuccessStatusCode)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.RequestAborted));
            connected = json.RootElement.TryGetProperty("service", out var service) && service.GetString() == "SimpleVoiceChat.WebMicrophone";
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { }
    return Results.Json(new { connected, message = connected ? "SimpleVoiceChat 已就绪" : "游戏服务器或 SimpleVoiceChat 未就绪，网页仍可正常使用。" });
});
app.Map("/voice", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var browser = await context.WebSockets.AcceptWebSocketAsync();
    using var backend = new ClientWebSocket();
    backend.Options.Proxy = null;
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await backend.ConnectAsync(new Uri($"ws://127.0.0.1:{settings.BackendPort}/voice"), timeout.Token);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
    {
        if (!context.RequestAborted.IsCancellationRequested)
        {
            await browser.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "error", message = "SimpleVoiceChat 未连接。请启动游戏服务器，并检查模组接入端口。" })), WebSocketMessageType.Text, true, context.RequestAborted);
            await browser.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Voice backend unavailable", context.RequestAborted);
        }
        return;
    }
    using var session = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    // Credentials and PCM are forwarded unchanged. Only the mod authenticates players and routes audio.
    var toBackend = Pump(browser, backend, session.Token);
    var toBrowser = Pump(backend, browser, session.Token);
    await Task.WhenAny(toBackend, toBrowser);
    await session.CancelAsync();
    try { await Task.WhenAll(toBackend, toBrowser); }
    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException) { }
});

try
{
    VoiceWebService.Validate(settings);
    await app.StartAsync();
    await WriteState(true);
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
    var heartbeat = Stopwatch.StartNew();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested && !File.Exists(stopPath))
    {
        if (heartbeat.Elapsed >= TimeSpan.FromSeconds(1))
        {
            try { await WriteState(true); heartbeat.Restart(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        try { await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping); }
        catch (OperationCanceledException) { break; }
    }
    await stopping.CancelAsync();
    await app.StopAsync();
    return 0;
}
catch (Exception ex) { state.Error = ex.Message; return 1; }
finally
{
    await stopping.CancelAsync();
    try { await WriteState(false); }
    finally { await app.DisposeAsync(); }
}

static async Task Pump(WebSocket source, WebSocket destination, CancellationToken token)
{
    var buffer = new byte[16 * 1024];
    while (!token.IsCancellationRequested)
    {
        var result = await source.ReceiveAsync(buffer, token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            if (destination.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await destination.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure, result.CloseStatusDescription, token);
            return;
        }
        await destination.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, token);
    }
}
