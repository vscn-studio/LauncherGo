using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LauncherGo.Domains.Models;
using Microsoft.AspNetCore.StaticFiles;

var arguments = args.Select((value, index) => (value, index))
    .Where(item => item.value.StartsWith("--", StringComparison.Ordinal) && item.index + 1 < args.Length)
    .ToDictionary(item => item.value[2..], item => args[item.index + 1], StringComparer.OrdinalIgnoreCase);
if (!arguments.TryGetValue("config", out var configPath) || !File.Exists(configPath))
    return 2;

var settings = JsonSerializer.Deserialize<ServerMapSettings>(await File.ReadAllTextAsync(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid ServerMap host configuration.");
var profileRoot = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
string Resolve(string value, string fallback) => Path.GetFullPath(string.IsNullOrWhiteSpace(value) ? fallback : Path.IsPathRooted(value) ? value : Path.Combine(profileRoot, value));

var builder = WebApplication.CreateSlimBuilder(args);
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
var defaultWebRoot = Path.Combine(AppContext.BaseDirectory, "WebRoot");
var webRoot = Resolve(settings.WebRoot, defaultWebRoot);
var contentTypes = new FileExtensionContentTypeProvider();
var backend = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
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

app.MapFallback(async context =>
{
    var relative = context.Request.Path.Value?.TrimStart('/') ?? string.Empty;
    if (string.IsNullOrWhiteSpace(relative)) relative = "index.html";
    var root = Path.GetFullPath(webRoot);
    var file = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
    {
        context.Response.StatusCode = 404;
        return;
    }
    if (contentTypes.TryGetContentType(file, out var type)) context.Response.ContentType = type;
    await context.Response.SendFileAsync(file, context.RequestAborted);
});

if (arguments.TryGetValue("stop", out var stopPath))
{
    _ = Task.Run(async () =>
    {
        while (!File.Exists(stopPath)) await Task.Delay(500);
        await app.StopAsync();
    });
}
await app.RunAsync();
return 0;
