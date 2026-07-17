using Winsomnia.Core;
using Winsomnia.Engine;

var enableLock = args.Contains("--enable-lock", StringComparer.OrdinalIgnoreCase);
var stateDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winsomnia");
var statePath = Path.Combine(stateDirectory, "state-v2.json");
var legacyPath = Path.Combine(stateDirectory, "config.json");

for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index].Equals("--state", StringComparison.OrdinalIgnoreCase))
        statePath = Path.GetFullPath(args[index + 1]);
    if (args[index].Equals("--legacy-config", StringComparison.OrdinalIgnoreCase))
        legacyPath = Path.GetFullPath(args[index + 1]);
}

var clock = new SystemClock();
var manager = new StateManager(statePath, clock);
if (args.Contains("--activate-state", StringComparer.OrdinalIgnoreCase))
{
    var offlineState = manager.LoadOrCreate(legacyPath) with { Armed = true };
    manager.Save(offlineState);
    Console.WriteLine("winsomnia engine state is armed; the kill switch remains unchanged.");
    return 0;
}
IWorkstationLocker locker = enableLock ? new WindowsWorkstationLocker() : new NoOpWorkstationLocker();
await using var host = new EngineHost(manager, clock, locker, enableLock, legacyPath);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try
{
    await host.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"winsomnia engine stopped safely: {exception.Message}");
    return 1;
}

return 0;
