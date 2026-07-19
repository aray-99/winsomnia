using Winsomnia.Core;
using Winsomnia.Engine;

var enableLock = args.Contains("--enable-lock", StringComparer.OrdinalIgnoreCase);
var stateDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winsomnia");
var statePath = Path.Combine(stateDirectory, "state-v3.json");
var legacyStatePath = Path.Combine(stateDirectory, "state-v2.json");
var legacyConfigPath = Path.Combine(stateDirectory, "config.json");

for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index].Equals("--state", StringComparison.OrdinalIgnoreCase))
        statePath = Path.GetFullPath(args[index + 1]);
    if (args[index].Equals("--legacy-state", StringComparison.OrdinalIgnoreCase))
        legacyStatePath = Path.GetFullPath(args[index + 1]);
    if (args[index].Equals("--legacy-config", StringComparison.OrdinalIgnoreCase))
        legacyConfigPath = Path.GetFullPath(args[index + 1]);
}

var clock = new SystemClock();
var manager = new StateManager(statePath, clock);
var markerStore = new LockMarkerStore();
if (args.Contains("--pause-state", StringComparer.OrdinalIgnoreCase))
{
    OfflineSafety.Pause(manager, markerStore, legacyStatePath, legacyConfigPath);
    Console.WriteLine("winsomnia engine state is disarmed and the lock marker is absent.");
    return 0;
}
if (args.Contains("--activate-state", StringComparer.OrdinalIgnoreCase))
{
    OfflineSafety.Activate(manager, markerStore, legacyStatePath, legacyConfigPath);
    Console.WriteLine("winsomnia engine state and lock marker are armed.");
    return 0;
}
IWorkstationLocker locker = enableLock ? new WindowsWorkstationLocker() : new NoOpWorkstationLocker();
await using var host = new EngineHost(manager, clock, locker, enableLock,
    legacyStatePath, legacyConfigPath, markerStore: markerStore);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

try { await host.RunAsync(shutdown.Token); }
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { return 0; }
catch (Exception exception)
{
    Console.Error.WriteLine($"winsomnia engine stopped safely: {exception.Message}");
    return 1;
}
return 0;
