using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace LauncherGo.Services;

internal sealed class MapLifecycleLog(ILogger logger, string profileId, string operation)
{
    private readonly string operationId = Guid.NewGuid().ToString("N")[..8];
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly Stopwatch stageClock = new();
    private string? stage;

    internal void Stage(string next)
    {
        EndStage();
        stage = next;
        stageClock.Restart();
        logger.LogInformation("Map lifecycle stage started. ProfileId={ProfileId}, Operation={Operation}, OperationId={OperationId}, Stage={Stage}.",
            profileId, operation, operationId, stage);
    }

    private void EndStage()
    {
        if (stage is not null)
            logger.LogInformation("Map lifecycle stage completed. ProfileId={ProfileId}, Operation={Operation}, OperationId={OperationId}, Stage={Stage}, ElapsedMs={ElapsedMs}, TotalMs={TotalMs}.",
                profileId, operation, operationId, stage, stageClock.ElapsedMilliseconds, total.ElapsedMilliseconds);
    }

    internal void Complete()
    {
        EndStage();
        stage = null;
        logger.LogInformation("Map lifecycle completed. ProfileId={ProfileId}, Operation={Operation}, OperationId={OperationId}, TotalMs={TotalMs}.",
            profileId, operation, operationId, total.ElapsedMilliseconds);
    }

    internal void Fail(Exception error) =>
        logger.LogWarning(error, "Map lifecycle failed. ProfileId={ProfileId}, Operation={Operation}, OperationId={OperationId}, Stage={Stage}, ElapsedMs={ElapsedMs}, TotalMs={TotalMs}, Cancelled={Cancelled}.",
            profileId, operation, operationId, stage, stageClock.ElapsedMilliseconds, total.ElapsedMilliseconds, error is OperationCanceledException);
}
