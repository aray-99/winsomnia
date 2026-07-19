namespace Winsomnia.Engine;

internal sealed class EngineInstanceLease : IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private readonly Thread ownerThread;
    private readonly TaskCompletionSource<bool> acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EngineInstanceLease(string name)
    {
        ownerThread = new Thread(() => OwnMutex(name)) { IsBackground = true, Name = "winsomnia-engine-mutex" };
        ownerThread.Start();
        if (!acquired.Task.GetAwaiter().GetResult())
        {
            release.Dispose();
            throw new InvalidOperationException("Another winsomnia Engine instance is already running for this user.");
        }
    }

    private void OwnMutex(string name)
    {
        using var mutex = new Mutex(false, name);
        var owns = false;
        try
        {
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            acquired.TrySetResult(owns);
            if (owns) release.Wait();
        }
        catch (Exception exception)
        {
            acquired.TrySetException(exception);
        }
        finally
        {
            if (owns) mutex.ReleaseMutex();
        }
    }

    public void Dispose()
    {
        release.Set();
        ownerThread.Join();
        release.Dispose();
    }
}
