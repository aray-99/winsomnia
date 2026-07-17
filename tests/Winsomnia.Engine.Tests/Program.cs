using System.Text.Json;
using Winsomnia.Core;
using Winsomnia.Engine;

var directory = Path.Combine(Path.GetTempPath(), $"winsomnia-engine-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
var killSwitch = Path.Combine(directory, "unlock.txt");
File.WriteAllText(killSwitch, string.Empty);
var pipeName = $"winsomnia.engine.test.{Guid.NewGuid():N}";
var clock = new SystemClock();
var manager = new StateManager(Path.Combine(directory, "state-v2.json"), clock);
var initial = new PersistentState
{
    KillSwitchPath = killSwitch,
    LogPath = Path.Combine(directory, "engine.log"),
    Armed = false,
    Credit = CreditLedger.Full(CreditPolicy.Standard, clock.UtcNow)
};
manager.Save(initial);

await using var host = new EngineHost(manager, clock, new NoOpWorkstationLocker(), false, pipeName: pipeName);
using var shutdown = new CancellationTokenSource();
var running = host.RunAsync(shutdown.Token);
try
{
    await Task.Delay(250);
    var client = new EngineClient(pipeName);
    var status = await client.GetStatusAsync();
    Assert(status.Paused, "Engine must start paused.");

    var started = await client.SendAsync<JsonElement>("startSession", new
    {
        kind = "focus",
        source = "integration-test",
        durationSeconds = 30,
        relockIntervalSeconds = 10,
        unlockGraceSeconds = 15,
        cancelable = true
    });
    var id = started.GetProperty("sessionId").GetGuid();
    var token = started.GetProperty("cancellationToken").GetString()!;

    await AssertThrowsAsync(() => client.SendAsync<JsonElement>("cancelSession", new
    {
        sessionId = id,
        token = new string('0', 64)
    }), "Wrong cancellation token was accepted.");

    await client.SendAsync<EngineStatus>("cancelSession", new { sessionId = id, token });
    status = await client.GetStatusAsync();
    Assert(status.ActiveSessions.Count == 0, "Canceled session remained active.");

    await client.RunSafeTestAsync();
    await client.ActivateAsync();
    status = await client.GetStatusAsync();
    Assert(status.Armed && !File.Exists(killSwitch), "Explicit activation did not arm and remove the file kill switch.");
    await client.PauseAsync();
    status = await client.GetStatusAsync();
    Assert(status.Paused && File.Exists(killSwitch), "Safety pause did not restore the kill switch.");
    Console.WriteLine("PASS named-pipe status, scoped cancellation, safe test, and explicit activation");
}
finally
{
    shutdown.Cancel();
    try { await running; } catch (OperationCanceledException) { }
    var full = Path.GetFullPath(directory);
    if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing to remove non-temporary test directory.");
    Directory.Delete(full, true);
}

return 0;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task AssertThrowsAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch { return; }
    throw new InvalidOperationException(message);
}
