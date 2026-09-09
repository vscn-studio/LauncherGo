using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.Server;

namespace ServerMap.World;

/// <summary>Adapter for the 1.22 save pipeline. Completion is observed on the
/// actual save methods, then delivered on a subsequent game tick. Exceptions
/// and unsupported layouts never acknowledge a generation.</summary>
public sealed class SaveCompletionAdapter : IDisposable
{
    private static SaveCompletionAdapter? active;
    private sealed record ReadState(long Epoch, bool Saving);
    private ReadState readState = new(0, false);
    public static bool SaveInProgress => active != null && Volatile.Read(ref active.readState).Saving;
    public static object CaptureReadFence()
    {
        var state = active == null ? new ReadState(0, false) : Volatile.Read(ref active.readState);
        if (state.Saving) throw new IOException("Waiting for world save completion");
        return state;
    }
    public static void ValidateReadFence(object fence)
    {
        if (active != null && !ReferenceEquals(fence, Volatile.Read(ref active.readState))) throw new IOException("World save changed during surface extraction; retrying the column");
    }
    private void Completed(Action completion)
    {
        if (ReferenceEquals(workerCompletion, completion)) { var old = Volatile.Read(ref readState); Volatile.Write(ref readState, new(old.Epoch, false)); }
        completions.Enqueue(completion);
    }
    private readonly Harmony harmony = new("launchergo.servermap.save-completion");
    private readonly Func<Action> begin;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> completions = new();
    private Action? workerCompletion;
    private readonly Action<string> error;
    private readonly object chunkThread;
    private readonly object savingGate;
    private readonly ServerMain server;
    private readonly FieldInfo offThread;
    private long begunAt;
    private bool timedOut;
    public SaveCompletionAdapter(ICoreServerAPI api, Func<Action> begin, Action<string> error)
    {
        this.begin = begin; this.error = error;
        server = (ServerMain)api.World;
        var type = typeof(ServerMain).Assembly.GetType("Vintagestory.Server.ServerSystemLoadAndSaveGame") ?? throw new NotSupportedException("Unknown game save system");
        chunkThread = AccessTools.Field(typeof(ServerMain), "chunkThread").GetValue(api.World)!;
        offThread = AccessTools.Field(chunkThread.GetType(), "runOffThreadSaveNow") ?? throw new NotSupportedException("Unknown asynchronous save state");
        var save = AccessTools.Method(type, "SaveGameWorld", [typeof(bool)]) ?? throw new NotSupportedException("Unknown save entry point");
        var tick = AccessTools.Method(type, "OnSeparateThreadTick") ?? throw new NotSupportedException("Unknown save worker");
        var saveSystem = AccessTools.Field(chunkThread.GetType(), "loadsavegame").GetValue(chunkThread) ?? throw new NotSupportedException("Save system is not initialized");
        savingGate = AccessTools.Field(type, "savingLock").GetValue(saveSystem) ?? throw new NotSupportedException("Unknown save synchronization");
        active = this;
        try
        {
        harmony.Patch(save, prefix: new HarmonyMethod(typeof(SaveCompletionAdapter), nameof(Begin)), finalizer: new HarmonyMethod(typeof(SaveCompletionAdapter), nameof(FinishMain)));
        harmony.Patch(tick, prefix: new HarmonyMethod(typeof(SaveCompletionAdapter), nameof(BeginWorker)), finalizer: new HarmonyMethod(typeof(SaveCompletionAdapter), nameof(FinishWorker)));
        }
        catch { harmony.UnpatchAll(harmony.Id); active = null; throw; }
    }
    private static void Begin(out Action? __state)
    {
        var a = active; __state = a?.begin(); if (a == null) return;
        a.workerCompletion = __state; Volatile.Write(ref a.readState, new(a.readState.Epoch + 1, true)); a.begunAt = Environment.TickCount64; a.timedOut = false;
    }
    private static Exception? FinishMain(bool saveLater, Action? __state, Exception? __exception)
    {
        if (__exception != null) active?.error("Save failed: " + __exception.Message);
        else if (!saveLater && __state != null && active is { } a && a.server.exitState.Mode != EnumExitMode.HardExit) a.Completed(__state);
        return __exception;
    }
    private sealed record WorkerState(SaveCompletionAdapter Adapter, Action? Completion);
    private static void BeginWorker(out WorkerState? __state)
    {
        var a = active; __state = null; if (a == null) return;
        // Use the engine's own lock across observation and its tick, preventing
        // the false -> true race between this prefix and the original method.
        Monitor.Enter(a.savingGate);
        __state = new(a, (bool)a.offThread.GetValue(a.chunkThread)! ? a.workerCompletion : null);
    }
    private static Exception? FinishWorker(WorkerState? __state, Exception? __exception)
    {
        if (__state is { } state)
        {
            var a = state.Adapter;
            try
            {
                if (state.Completion != null)
                {
                    if (__exception != null) a.error("Asynchronous save failed: " + __exception.Message);
                    else if (!(bool)a.offThread.GetValue(a.chunkThread)!) a.Completed(state.Completion);
                }
            }
            finally { Monitor.Exit(a.savingGate); }
        }
        return __exception;
    }
    public void Tick()
    {
        while (completions.TryDequeue(out var completion)) { begunAt = 0; completion(); }
        if (begunAt != 0 && !timedOut && Environment.TickCount64 - begunAt > 120000) { timedOut = true; error("Waiting for confirmed world save (over 120 seconds)."); }
    }
    public void Dispose() { harmony.UnpatchAll(harmony.Id); if (ReferenceEquals(active, this)) active = null; }
}
