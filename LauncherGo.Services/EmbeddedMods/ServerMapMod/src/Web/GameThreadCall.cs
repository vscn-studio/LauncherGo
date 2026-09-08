namespace ServerMap.Web;

/// <summary>A timed-out queued mutation cannot execute later.</summary>
public sealed class GameThreadCall<T>(Func<T> action)
{
    private readonly TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int state;
    public Task<T> Task => result.Task;
    public void Run()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
        try { result.TrySetResult(action()); }
        catch (Exception ex) { result.TrySetException(ex); }
    }
    public bool CancelPending()
    {
        if (Interlocked.CompareExchange(ref state, 2, 0) != 0) return false;
        result.TrySetCanceled(); return true;
    }
}
