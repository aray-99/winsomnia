using System.Diagnostics;
using System.Text.Json;
using Winsomnia.Core;
using Winsomnia.Engine;

var tests = new (string Name, Func<Task> Run)[]
{
    ("marker validation fails closed", TestMarkerValidationAsync),
    ("offline activation and pause order safely", TestOfflineTransitionsAsync),
    ("IPC activation and pause expose authorization", TestIpcTransitionsAsync),
    ("activation and pause failures latch denial", TestTransitionFailuresAsync),
    ("activation has no fallible post-commit save", TestNoPostCommitSaveAsync),
    ("concurrent clients serialize state transitions", TestConcurrentClientsAsync),
    ("monitor uses fixed monotonic cadence", TestMonitorCadenceAsync),
    ("immediate pre-lock marker check denies", TestImmediatePreLockCheckAsync),
    ("single engine mutex rejects a second host", TestSingleEngineAsync),
    ("offline CLI rejects a live Engine", TestOfflineCliLeaseAsync)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static async Task TestMarkerValidationAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid().ToString("N");
    var state = fixture.ArmedState(id);
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Disarmed, "marker-missing");
    File.WriteAllText(fixture.MarkerPath, "not-json");
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-malformed");
    File.WriteAllText(fixture.MarkerPath, JsonSerializer.Serialize(new { version = 2, activationId = id }));
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-version-unsupported");
    File.WriteAllText(fixture.MarkerPath, JsonSerializer.Serialize(new { version = 1, activationId = Guid.NewGuid().ToString("N") }));
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-activation-id-mismatch");
    File.WriteAllText(fixture.MarkerPath, JsonSerializer.Serialize(new { version = 1, activationId = id, extra = true }));
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-malformed");
    File.WriteAllText(fixture.MarkerPath, new string('x', 4097));
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-too-large");
    File.Delete(fixture.MarkerPath);
    Directory.CreateDirectory(fixture.MarkerPath);
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-is-directory");
    Directory.Delete(fixture.MarkerPath);

    var target = Path.Combine(fixture.Directory, "target.json");
    File.WriteAllText(target, JsonSerializer.Serialize(new { version = 1, activationId = id }));
    File.CreateSymbolicLink(fixture.MarkerPath, target);
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-is-reparse-point");
    File.Delete(fixture.MarkerPath);

    fixture.Store.Commit(id);
    using (File.Open(fixture.MarkerPath, FileMode.Open, FileAccess.Read, FileShare.None))
        AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Faulted, "marker-io-failure");
    AssertState(fixture.Store.Inspect(state, true), LockAuthorizationStates.Armed, "marker-validated");

    var locker = new CountingLocker();
    File.WriteAllText(fixture.MarkerPath, "broken");
    fixture.Manager.Save(state);
    await RunBrieflyAsync(fixture, locker, fixture.Store, true);
    Assert(locker.Requests == 0, "Malformed marker reached the lock helper.");
}

static Task TestOfflineTransitionsAsync()
{
    using var fixture = new Fixture();
    OfflineSafety.Activate(fixture.Manager, fixture.Store);
    var armed = fixture.Manager.LoadOrCreate();
    Assert(armed.Armed && armed.ActivationId is not null, "Offline activate did not persist armed state.");
    AssertState(fixture.Store.Inspect(armed, true), LockAuthorizationStates.Armed, "marker-validated");
    OfflineSafety.Pause(fixture.Manager, fixture.Store);
    var paused = fixture.Manager.LoadOrCreate();
    Assert(!paused.Armed && paused.ActivationId is null && !File.Exists(fixture.MarkerPath),
        "Offline pause did not revoke marker and clear state.");

    OfflineSafety.Activate(fixture.Manager, fixture.Store);
    var failingRevoke = new FailingMarkerStore(fixture.Store) { FailRevoke = true };
    AssertThrows(() => OfflineSafety.Pause(fixture.Manager, failingRevoke),
        "Offline marker revoke failure was accepted.");
    paused = fixture.Manager.LoadOrCreate();
    Assert(!paused.Armed && paused.ActivationId is null,
        "Offline revoke failure did not durably disarm state.");
    return Task.CompletedTask;
}

static async Task TestIpcTransitionsAsync()
{
    using var fixture = new Fixture();
    var pipe = $"winsomnia.engine.test.{Guid.NewGuid():N}";
    var mutex = $"Local\\winsomnia.engine.test.{Guid.NewGuid():N}";
    await using var host = new EngineHost(fixture.Manager, fixture.Clock, new CountingLocker(), false,
        pipeName: pipe, markerStore: fixture.Store, mutexName: mutex);
    using var shutdown = new CancellationTokenSource();
    var running = host.RunAsync(shutdown.Token);
    try
    {
        await Task.Delay(150);
        var client = new EngineClient(pipe);
        var initial = await client.GetStatusAsync();
        Assert(initial.Paused && initial.LockAuthorization.State == LockAuthorizationStates.Disarmed,
            "Engine did not start disarmed.");
        await client.RunSafeTestAsync();
        var armed = await client.ActivateAsync();
        Assert(armed.Armed && armed.LockAuthorization.Reason == "lock-switch-disabled",
            "Dry-run Engine did not expose persisted armed state without authorizing locks.");
        var paused = await client.PauseAsync();
        Assert(paused.Paused && !paused.Armed && !File.Exists(fixture.MarkerPath), "IPC pause was not safe.");
    }
    finally { await StopAsync(shutdown, running); }
}

static async Task TestTransitionFailuresAsync()
{
    using var fixture = new Fixture();
    var failingCommit = new FailingMarkerStore(fixture.Store) { FailCommit = true };
    var clientRun = await StartHostAsync(fixture, failingCommit, true);
    try
    {
        await AssertThrowsAsync(() => clientRun.Client.ActivateAsync(), "Failed marker commit was accepted.");
        var state = fixture.Manager.LoadOrCreate();
        Assert(!state.Armed && state.ActivationId is null, "Failed activation did not return to disarmed state.");
        Assert(clientRun.Locker.Requests == 0, "Failed activation reached locker.");
    }
    finally { await clientRun.DisposeAsync(); }

    var id = Guid.NewGuid().ToString("N");
    fixture.Manager.Save(fixture.ArmedState(id));
    fixture.Store.Commit(id);
    var failingRevoke = new FailingMarkerStore(fixture.Store) { FailRevoke = true };
    clientRun = await StartHostAsync(fixture, failingRevoke, true);
    var requestsBeforePause = clientRun.Locker.Requests;
    try
    {
        await AssertThrowsAsync(() => clientRun.Client.PauseAsync(), "Failed marker revoke was accepted.");
        var status = await clientRun.Client.GetStatusAsync();
        Assert(status.LockAuthorization.State == LockAuthorizationStates.Faulted &&
            status.LockAuthorization.Reason == "runtime-denial-latched", "Pause failure did not latch denial.");
        await Task.Delay(1100);
        Assert(clientRun.Locker.Requests == requestsBeforePause, "Pause failure reached locker after denial latch.");
    }
    finally { await clientRun.DisposeAsync(); }

    var durable = fixture.Manager.LoadOrCreate();
    Assert(!durable.Armed && durable.ActivationId is null && File.Exists(fixture.MarkerPath),
        "Marker revoke failure did not durably disarm state.");
    var restartedLocker = new CountingLocker();
    await RunBrieflyAsync(fixture, restartedLocker, fixture.Store, true);
    Assert(restartedLocker.Requests == 0, "Restart reauthorized a marker after pause failure.");
}

static async Task TestNoPostCommitSaveAsync()
{
    using var fixture = new Fixture();
    using var store = new PostCommitStateLockMarkerStore(fixture.Store, fixture.Manager.StatePath);
    var run = await StartHostAsync(fixture, store, false);
    try
    {
        var status = await run.Client.ActivateAsync();
        Assert(status.Armed, "Activation failed after its marker commit.");
        AssertState(fixture.Store.Inspect(fixture.Manager.LoadOrCreate(), true),
            LockAuthorizationStates.Armed, "marker-validated");
    }
    finally { await run.DisposeAsync(); }
}

static async Task TestConcurrentClientsAsync()
{
    using var fixture = new Fixture();
    var run = await StartHostAsync(fixture, fixture.Store, false);
    try
    {
        var activations = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => run.Client.ActivateAsync()));
        Assert(activations.All(status => status.Armed), "A serialized activation failed.");
        AssertState(fixture.Store.Inspect(fixture.Manager.LoadOrCreate(), true),
            LockAuthorizationStates.Armed, "marker-validated");
        var pauses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => run.Client.PauseAsync()));
        Assert(pauses.All(status => status.Paused && !status.Armed), "A serialized pause failed.");
        var state = fixture.Manager.LoadOrCreate();
        Assert(!state.Armed && state.ActivationId is null && !File.Exists(fixture.MarkerPath),
            "Concurrent clients left inconsistent authorization.");
    }
    finally { await run.DisposeAsync(); }
}

static async Task TestMonitorCadenceAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid().ToString("N");
    fixture.Manager.Save(fixture.ArmedState(id));
    var store = new SlowRecordingMarkerStore();
    using var stop = new CancellationTokenSource();
    await using var host = new EngineHost(fixture.Manager, fixture.Clock, new CountingLocker(), true,
        pipeName: $"winsomnia.engine.test.{Guid.NewGuid():N}", markerStore: store,
        mutexName: $"Local\\winsomnia.engine.test.{Guid.NewGuid():N}");
    var running = host.RunAsync(stop.Token);
    await Task.Delay(3200);
    await StopAsync(stop, running);
    var inspections = store.Inspections;
    Assert(inspections.Count >= 4, "Monitor did not run enough cadence checks.");
    var steadyGaps = inspections.Zip(inspections.Skip(1), Stopwatch.GetElapsedTime).Skip(1).ToList();
    Assert(steadyGaps.All(gap => gap <= TimeSpan.FromMilliseconds(1150)),
        $"Monitor cadence included a work-plus-delay gap: {steadyGaps.Max()}.");
}
static async Task TestImmediatePreLockCheckAsync()
{
    using var fixture = new Fixture();
    var id = Guid.NewGuid().ToString("N");
    fixture.Manager.Save(fixture.ArmedState(id));
    var store = new SequencedMarkerStore();
    var locker = new CountingLocker();
    await RunBrieflyAsync(fixture, locker, store, true);
    Assert(store.Inspections >= 2, "Marker was not checked immediately before locking.");
    Assert(locker.Requests == 0, "Revoked marker reached locker during pre-lock race.");
}

static async Task TestSingleEngineAsync()
{
    using var fixture = new Fixture();
    var mutex = $"Local\\winsomnia.engine.test.{Guid.NewGuid():N}";
    using var firstStop = new CancellationTokenSource();
    await using var first = new EngineHost(fixture.Manager, fixture.Clock, new CountingLocker(), false,
        pipeName: $"winsomnia.engine.test.{Guid.NewGuid():N}", markerStore: fixture.Store, mutexName: mutex);
    var firstRun = first.RunAsync(firstStop.Token);
    await Task.Delay(150);
    await using var second = new EngineHost(fixture.Manager, fixture.Clock, new CountingLocker(), false,
        pipeName: $"winsomnia.engine.test.{Guid.NewGuid():N}", markerStore: fixture.Store, mutexName: mutex);
    await AssertThrowsAsync(() => second.RunAsync(CancellationToken.None), "Second Engine acquired the same mutex.");
    await StopAsync(firstStop, firstRun);
}

static async Task TestOfflineCliLeaseAsync()
{
    using var fixture = new Fixture();
    var statePath = Path.Combine(fixture.Directory, "offline-state-v3.json");
    using var stop = new CancellationTokenSource();
    await using var host = new EngineHost(fixture.Manager, fixture.Clock, new CountingLocker(), false,
        pipeName: $"winsomnia.engine.test.{Guid.NewGuid():N}", markerStore: fixture.Store,
        mutexName: EngineHost.GetMutexName());
    var running = host.RunAsync(stop.Token);
    try
    {
        await Task.Delay(150);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add(typeof(EngineHost).Assembly.Location);
        start.ArgumentList.Add("--pause-state");
        start.ArgumentList.Add("--state");
        start.ArgumentList.Add(statePath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Engine process did not start.");
        await process.WaitForExitAsync();
        Assert(process.ExitCode != 0, "Offline CLI mutated state while a live Engine owned the lease.");
        Assert(!File.Exists(statePath), "Rejected offline CLI created state before acquiring the lease.");
    }
    finally { await StopAsync(stop, running); }
}
static async Task RunBrieflyAsync(Fixture fixture, CountingLocker locker, ILockMarkerStore store, bool enabled)
{
    using var stop = new CancellationTokenSource();
    await using var host = new EngineHost(fixture.Manager, fixture.Clock, locker, enabled,
        pipeName: $"winsomnia.engine.test.{Guid.NewGuid():N}", markerStore: store,
        mutexName: $"Local\\winsomnia.engine.test.{Guid.NewGuid():N}");
    var run = host.RunAsync(stop.Token);
    await Task.Delay(250);
    await StopAsync(stop, run);
}

static async Task<HostRun> StartHostAsync(Fixture fixture, ILockMarkerStore store, bool enabled)
{
    var locker = new CountingLocker();
    var stop = new CancellationTokenSource();
    var pipe = $"winsomnia.engine.test.{Guid.NewGuid():N}";
    var host = new EngineHost(fixture.Manager, fixture.Clock, locker, enabled,
        pipeName: pipe, markerStore: store,
        mutexName: $"Local\\winsomnia.engine.test.{Guid.NewGuid():N}");
    var run = host.RunAsync(stop.Token);
    await Task.Delay(150);
    return new HostRun(host, stop, run, new EngineClient(pipe), locker);
}

static async Task StopAsync(CancellationTokenSource stop, Task run)
{
    stop.Cancel();
    try { await run; } catch (OperationCanceledException) { }
}

static void AssertState(LockAuthorization actual, string state, string reason) =>
    Assert(actual.State == state && actual.Reason == reason,
        $"Expected {state}/{reason}, got {actual.State}/{actual.Reason}.");
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void AssertThrows(Action action, string message)
{
    try { action(); } catch { return; }
    throw new InvalidOperationException(message);
}
static async Task AssertThrowsAsync(Func<Task> action, string message)
{
    try { await action(); } catch { return; }
    throw new InvalidOperationException(message);
}

sealed class FixedClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; } = new(2026, 7, 17, 14, 30, 0, TimeSpan.Zero);
    public DateTimeOffset LocalNow => new(2026, 7, 17, 23, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 7, 17, 23, 30, 0)));
}
sealed class CountingLocker : IWorkstationLocker
{
    public int Requests { get; private set; }
    public void Lock() => Requests++;
}
sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"winsomnia-engine-tests-{Guid.NewGuid():N}");
    public string MarkerPath => Path.Combine(Directory, "lock-enabled.json");
    public FixedClock Clock { get; } = new();
    public StateManager Manager { get; }
    public LockMarkerStore Store { get; }
    public Fixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Manager = new StateManager(Path.Combine(Directory, "state-v3.json"), Clock);
        Store = new LockMarkerStore(MarkerPath);
    }
    public PersistentState ArmedState(string id)
    {
        var session = LockSession.Create(SessionKind.Focus, "engine-test", Clock.UtcNow,
            TimeSpan.FromMinutes(5), 1, 0, true).Session;
        return new PersistentState
        {
            Armed = true,
            ActivationId = id,
            Sessions = [session],
            LogPath = Path.Combine(Directory, "engine.log"),
            Credit = CreditLedger.Full(CreditPolicy.Standard, Clock.UtcNow)
        };
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(Directory);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove non-temporary test directory.");
        System.IO.Directory.Delete(full, true);
    }
}
sealed class FailingMarkerStore(ILockMarkerStore inner) : ILockMarkerStore
{
    public bool FailCommit { get; init; }
    public bool FailRevoke { get; init; }
    public LockAuthorization Inspect(PersistentState state, bool enabled) => inner.Inspect(state, enabled);
    public void Commit(string id) { if (FailCommit) throw new IOException("injected commit failure"); inner.Commit(id); }
    public void Revoke() { if (FailRevoke) throw new IOException("injected revoke failure"); inner.Revoke(); }
}
sealed class SequencedMarkerStore : ILockMarkerStore
{
    public int Inspections { get; private set; }
    public LockAuthorization Inspect(PersistentState state, bool enabled) => ++Inspections == 1
        ? new(LockAuthorizationStates.Armed, "marker-validated")
        : new(LockAuthorizationStates.Disarmed, "marker-missing");
    public void Commit(string id) { }
    public void Revoke() { }
}
sealed class PostCommitStateLockMarkerStore(ILockMarkerStore inner, string statePath) : ILockMarkerStore, IDisposable
{
    private FileStream? stateLock;
    public LockAuthorization Inspect(PersistentState state, bool enabled) => inner.Inspect(state, enabled);
    public void Commit(string id)
    {
        inner.Commit(id);
        stateLock = File.Open(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    public void Revoke() => inner.Revoke();
    public void Dispose() => stateLock?.Dispose();
}
sealed class SlowRecordingMarkerStore : ILockMarkerStore
{
    private readonly object sync = new();
    private readonly List<long> inspections = [];
    public IReadOnlyList<long> Inspections
    {
        get { lock (sync) return inspections.ToList(); }
    }
    public LockAuthorization Inspect(PersistentState state, bool enabled)
    {
        lock (sync) inspections.Add(Stopwatch.GetTimestamp());
        Thread.Sleep(300);
        return new(LockAuthorizationStates.Armed, "marker-validated");
    }
    public void Commit(string id) { }
    public void Revoke() { }
}sealed class HostRun(EngineHost host, CancellationTokenSource stop, Task run, EngineClient client, CountingLocker locker) : IAsyncDisposable
{
    public EngineClient Client { get; } = client;
    public CountingLocker Locker { get; } = locker;
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await run; } catch (OperationCanceledException) { }
        await host.DisposeAsync();
        stop.Dispose();
    }
}
