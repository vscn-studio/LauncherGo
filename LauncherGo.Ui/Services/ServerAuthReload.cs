using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Services;

public static class ServerAuthReload
{
    public static async Task<bool> ReloadAsync(
        IServerProcessService processService,
        string profileId,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = processService.GetCurrentStatus(profileId);
        if (!status.IsRunning)
            return false;
        if (!status.CanSendCommands)
            throw new InvalidOperationException("服务器命令通道不可用 / Server command channel unavailable.");

        var requestId = Guid.NewGuid().ToString("N");
        var marker = "[SERVER-AUTH] reload " + requestId + " ";
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnOutput(object? sender, ServerOutputLine output)
        {
            if (!string.Equals(output.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
                return;
            var index = output.Line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
                return;
            var result = output.Line[(index + marker.Length)..].Trim();
            if (result == "OK")
                completion.TrySetResult(true);
            else if (result.StartsWith("ERROR ", StringComparison.Ordinal))
                completion.TrySetException(new InvalidOperationException(result[6..]));
        }

        processService.ProfileOutputReceived += OnOutput;
        try
        {
            await processService.SendCommandAsync(profileId, "/serverauth reload " + requestId, cancellationToken);
            return await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("未收到认证重载结果；请确认 ServerAuth 1.1.1 已加载，首次更新需重启服务器 / No auth reload response; load ServerAuth 1.1.1 and restart the server after the initial update.");
        }
        finally
        {
            processService.ProfileOutputReceived -= OnOutput;
        }
    }
}
