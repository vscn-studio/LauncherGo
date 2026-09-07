using System.Collections.Concurrent;
using System.Text;
using System.Net;

namespace ServerMap.Web;

public sealed class LiveEventHub : IDisposable
{
    private readonly ConcurrentDictionary<Guid, HttpListenerResponse> clients = new();
    public async Task Subscribe(HttpListenerContext context, CancellationToken cancellation)
    {
        context.Response.StatusCode = 200; context.Response.ContentType = "text/event-stream"; context.Response.Headers.Add("Cache-Control", "no-cache"); context.Response.SendChunked = true;
        var id = Guid.NewGuid(); clients[id] = context.Response;
        try { await Write(context.Response, "ready", "{}"); await Task.Delay(Timeout.Infinite, cancellation); }
        catch (OperationCanceledException) { }
        catch { }
        finally { clients.TryRemove(id, out _); try { context.Response.Close(); } catch { } }
    }
    public void Publish(string type, object value)
    {
        var data = System.Text.Json.JsonSerializer.Serialize(value);
        foreach (var pair in clients) _ = Task.Run(async () => { try { await Write(pair.Value, type, data); } catch { clients.TryRemove(pair.Key, out _); try { pair.Value.Close(); } catch { } } });
    }
    private static async Task Write(HttpListenerResponse response, string type, string data) => await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes($"event: {type}\ndata: {data}\n\n"));
    public void Dispose() { foreach (var response in clients.Values) try { response.Close(); } catch { } clients.Clear(); }
}
