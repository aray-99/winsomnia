using System.Text;
using Winsomnia.Setup;

TestTaskDefinition();
TestSuccessfulCutoverOrder();
foreach (var failure in new[]
{
    "Switch", "Disable:winsomnia", "End:winsomnia", "AssertProcesses", "Disarm", "Marker", "AssertDisarmed",
    "Stage", "Swap", "Register", "PostTaskQuery", "Shortcut"
}) TestFailureReturnsToBarrier(failure);
TestV2MigrationIsReadOnlyAndDisarmed();
TestStateFailureStillRevokesMarker();
TestPayloadStagingAndValidation();
TestAtomicSwapRollbackAndCommit();
TestSetupLeaseRejectsConcurrentOwner();
TestEngineLeaseRejectsConcurrentSafetyMutation();

Console.WriteLine("PASS setup cutover is disabled, disarmed, staged, reversible, and fail-safe");
return 0;

static void TestTaskDefinition()
{
    var engine = @"C:\Program Files\winsomnia\Winsomnia.Engine.exe";
    var plan = SetupTaskPlan.Define("winsomnia", engine, @"COMPUTER\USER");
    Assert(plan.TaskName == "winsomnia" && plan.EnginePath == engine, "Task identity changed.");
    Assert(plan.AllowStartOnBattery && !plan.StopOnBattery && plan.RestartCount == 3,
        "Task runtime policy changed.");
    Assert(!plan.Enabled, "A setup task may never be registered enabled.");
    AssertThrows(() => SetupTaskPlan.Define("winsomnia", "Winsomnia.Engine.exe", @"COMPUTER\USER"),
        "A relative engine path was accepted.");
    AssertThrows(() => SetupTaskPlan.Define("winsomnia", engine, ""), "An empty task principal was accepted.");
}

static void TestSuccessfulCutoverOrder()
{
    var fake = new FakePlatform();
    new SetupCoordinator(fake, Paths()).Install();
    var expectedPrefix = new[]
    {
        "Lease", "Switch", "Disable:winsomnia", "Disable:win-somnia", "End:winsomnia", "End:win-somnia",
        "AssertProcesses", "Disarm", "AssertDisarmed", "TaskQuery:winsomnia", "TaskQuery:win-somnia", "Stage", "Swap",
        "Register"
    };
    Assert(fake.Events.Take(expectedPrefix.Length).SequenceEqual(expectedPrefix),
        "Safe cutover order changed: " + string.Join(",", fake.Events));
    Assert(fake.KillSwitch && !fake.Marker && !fake.Armed && !fake.ProcessRunning,
        "Successful setup did not leave the runtime at a safe barrier.");
    Assert(fake.Tasks.Values.All(enabled => !enabled), "Successful setup left a task enabled.");
    Assert(fake.RegisteredPlan is { Enabled: false }, "The registered task was not explicitly disabled.");
    Assert(fake.SwapCommitted, "The validated installation was not committed.");
}

static void TestFailureReturnsToBarrier(string failure)
{
    var fake = new FakePlatform { FailOnce = failure };
    AssertThrows(() => new SetupCoordinator(fake, Paths()).Install(), $"Injected failure '{failure}' was ignored.");
    Assert(fake.BarrierRuns >= 2, $"Failure '{failure}' did not rerun the safety barrier.");
    Assert(fake.KillSwitch && !fake.Marker && !fake.Armed && !fake.ProcessRunning,
        $"Failure '{failure}' did not leave marker/state/process safe.");
    Assert(fake.Tasks.Values.All(enabled => !enabled), $"Failure '{failure}' left a task enabled.");
    if (failure is "Register" or "PostTaskQuery" or "Shortcut")
        Assert(fake.SwapRolledBack, "A post-swap failure did not roll back binaries.");
}

static void TestV2MigrationIsReadOnlyAndDisarmed()
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-setup-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var v2 = Path.Combine(root, "state-v2.json");
        File.WriteAllText(v2, """
        {
          "schemaVersion": 2,
          "settings": {
            "startTime": "22:30",
            "endTime": "06:30",
            "enabled": true,
            "relockIntervalSeconds": 7,
            "credit": { "dailyGrantMinutes": 5, "maximumMinutes": 30 }
          }
        }
        """, new UTF8Encoding(false));
        var original = File.ReadAllBytes(v2);
        var marker = Path.Combine(root, "marker.json");
        File.WriteAllText(marker, "stale marker");
        var paths = Paths(root) with
        {
            StatePath = Path.Combine(root, "state-v3.json"),
            LegacyStatePath = v2,
            LegacyConfigPath = Path.Combine(root, "config.json"),
            MarkerPath = marker
        };
        var platform = new WindowsSetupPlatform();
        platform.DisarmEngine(paths);
        platform.AssertEngineDisarmed(paths);
        Assert(File.ReadAllBytes(v2).SequenceEqual(original), "Version 2 state bytes were modified.");
        var v3 = File.ReadAllText(paths.StatePath);
        Assert(v3.Contains("\"armed\": false", StringComparison.OrdinalIgnoreCase), "Migrated v3 state is not disarmed.");
        Assert(v3.Contains("22:30", StringComparison.Ordinal), "Version 2 settings were not imported.");
        Assert(!File.Exists(marker), "Stale marker was not revoked.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestStateFailureStillRevokesMarker()
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-state-failure-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var paths = Paths(root);
        File.WriteAllText(paths.StatePath, "{ invalid state");
        File.WriteAllText(paths.MarkerPath, "stale marker");
        AssertThrows(() => new WindowsSetupPlatform().DisarmEngine(paths),
            "Corrupt state unexpectedly completed disarm.");
        Assert(!File.Exists(paths.MarkerPath), "State failure prevented independent marker revocation.");
    }
    finally { Directory.Delete(root, true); }
}
static void TestPayloadStagingAndValidation()
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-payload-test-{Guid.NewGuid():N}");
    var source = Path.Combine(root, "app");
    Directory.CreateDirectory(source);
    try
    {
        foreach (var file in new[] { "Winsomnia.Engine.exe", "Winsomnia.Desktop.exe", "VERSION" })
            File.WriteAllText(Path.Combine(source, file), file == "VERSION" ? "0.3.0" : "payload");
        var setup = Path.Combine(root, "source-setup.exe");
        File.WriteAllText(setup, "setup");
        var paths = Paths(root) with { Source = source, SetupExecutable = setup, Target = Path.Combine(root, "installed") };
        var platform = new WindowsSetupPlatform();
        var stage = platform.StageAndValidatePayload(paths);
        Assert(File.Exists(Path.Combine(stage, "Winsomnia.Setup.exe")) && File.Exists(Path.Combine(stage, "VERSION")),
            "Validated staging omitted required payload files.");
        Directory.Delete(stage, true);

        File.Delete(Path.Combine(source, "Winsomnia.Engine.exe"));
        AssertThrows(() => platform.StageAndValidatePayload(paths), "An incomplete payload passed staging validation.");
        Assert(!Directory.GetDirectories(root, ".winsomnia-stage-*").Any(),
            "Failed payload validation left a staging directory behind.");
    }
    finally { Directory.Delete(root, true); }
}
static void TestAtomicSwapRollbackAndCommit()
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-swap-test-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var target = Path.Combine(root, "winsomnia");
        var stage = Path.Combine(root, ".stage-one");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
        using (new WindowsSetupPlatform().ReplaceInstallation(stage, target)) { }
        Assert(File.Exists(Path.Combine(target, "old.txt")) && !File.Exists(Path.Combine(target, "new.txt")),
            "Interrupted replacement did not restore the old installation.");

        stage = Path.Combine(root, ".stage-two");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
        using var swap = new WindowsSetupPlatform().ReplaceInstallation(stage, target);
        swap.Commit();
        Assert(File.Exists(Path.Combine(target, "new.txt")) && !File.Exists(Path.Combine(target, "old.txt")),
            "Committed replacement did not install the staged payload.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestSetupLeaseRejectsConcurrentOwner()
{
    using var first = new WindowsSetupPlatform().AcquireSetupLease();
    var rejected = Task.Run(() =>
    {
        try { using var ignored = new WindowsSetupPlatform().AcquireSetupLease(); return false; }
        catch (InvalidOperationException) { return true; }
    }).GetAwaiter().GetResult();
    Assert(rejected, "Concurrent setup acquired the setup mutex.");
}

static void TestEngineLeaseRejectsConcurrentSafetyMutation()
{
    using var acquired = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    Exception? ownerFailure = null;
    var owner = new Thread(() =>
    {
        try
        {
            using var mutex = new Mutex(false, Winsomnia.Engine.EngineHost.GetMutexName());
            var owns = false;
            try { owns = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owns = true; }
            if (!owns) throw new InvalidOperationException("Could not acquire the Engine test mutex.");
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        }
        catch (Exception exception) { ownerFailure = exception; acquired.Set(); }
    });
    owner.Start();
    acquired.Wait();
    try
    {
        if (ownerFailure is not null) throw ownerFailure;
        var root = Path.Combine(Path.GetTempPath(), $"winsomnia-engine-lease-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            AssertThrows(() => new WindowsSetupPlatform().DisarmEngine(Paths(root)),
                "Setup mutated Engine state while the Engine mutex was owned.");
        }
        finally { Directory.Delete(root, true); }
    }
    finally
    {
        release.Set();
        owner.Join();
    }
}
static SetupPaths Paths(string? root = null)
{
    root ??= @"C:\package";
    return new(Path.Combine(root, "app"), Path.Combine(root, "Winsomnia.Setup.exe"),
        Path.Combine(root, "installed"), Path.Combine(root, "legacy-switch"), Path.Combine(root, "marker"),
        Path.Combine(root, "state-v3.json"), Path.Combine(root, "state-v2.json"), Path.Combine(root, "config.json"),
        Path.Combine(root, "start-menu.lnk"), Path.Combine(root, "startup.lnk"));
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void AssertThrows(Action action, string message)
{
    try { action(); }
    catch { return; }
    throw new InvalidOperationException(message);
}

sealed class FakePlatform : ISetupPlatform
{
    public List<string> Events { get; } = [];
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winsomnia"] = true,
        ["win-somnia"] = true
    };
    public string? FailOnce { get; init; }
    public bool KillSwitch { get; private set; }
    public bool Marker { get; private set; } = true;
    public bool Armed { get; private set; } = true;
    public bool ProcessRunning { get; private set; } = true;
    public int BarrierRuns { get; private set; }
    public bool SwapCommitted { get; private set; }
    public bool SwapRolledBack { get; private set; }
    public ScheduledTaskPlan? RegisteredPlan { get; private set; }
    private bool failureConsumed;
    private int taskQueries;

    public IDisposable AcquireSetupLease() { Events.Add("Lease"); return new Lease(); }
    public void EnsureLegacyKillSwitch(string path)
    {
        Events.Add("Switch");
        BarrierRuns++;
        KillSwitch = true;
        Fail("Switch");
    }
    public void DisableTask(string taskName)
    {
        Events.Add($"Disable:{taskName}");
        Tasks[taskName] = false;
        Fail($"Disable:{taskName}");
    }
    public void EndTask(string taskName)
    {
        Events.Add($"End:{taskName}");
        if (taskName == "win-somnia") ProcessRunning = false;
        Fail($"End:{taskName}");
    }
    public void DeleteTask(string taskName) { Events.Add($"Delete:{taskName}"); Tasks.Remove(taskName); }
    public bool IsTaskDisabledOrMissing(string taskName)
    {
        Events.Add($"TaskQuery:{taskName}");
        taskQueries++;
        if (FailOnce == "PostTaskQuery" && RegisteredPlan is not null && taskQueries > 2) Fail("PostTaskQuery");
        return !Tasks.TryGetValue(taskName, out var enabled) || !enabled;
    }
    public void AssertNoRuntimeProcesses(string installedRoot)
    {
        Events.Add("AssertProcesses");
        Fail("AssertProcesses");
        if (ProcessRunning) throw new InvalidOperationException("runtime remains");
    }
    public void DisarmEngine(SetupPaths paths)
    {
        Events.Add("Disarm");
        Fail("Disarm");
        Armed = false;
        Fail("Marker");
        Marker = false;
    }
    public void AssertEngineDisarmed(SetupPaths paths)
    {
        Events.Add("AssertDisarmed");
        Fail("AssertDisarmed");
        if (Armed || Marker) throw new InvalidOperationException("armed");
    }
    public string StageAndValidatePayload(SetupPaths paths) { Events.Add("Stage"); Fail("Stage"); return "stage"; }
    public IInstallationSwap ReplaceInstallation(string stagedPath, string targetPath)
    {
        Events.Add("Swap");
        Fail("Swap");
        return new FakeSwap(this);
    }
    public void RegisterDisabledTask(ScheduledTaskPlan plan)
    {
        Events.Add("Register");
        RegisteredPlan = plan;
        Tasks[plan.TaskName] = false;
        Fail("Register");
    }
    public void CreateShortcut(string path, string target, string arguments)
    {
        Events.Add("Shortcut");
        Fail("Shortcut");
    }
    public void DeleteShortcut(string path) => Events.Add("DeleteShortcut");
    public void DeleteInstallation(string targetPath) => Events.Add("DeleteInstallation");

    private void Fail(string point)
    {
        if (failureConsumed || FailOnce != point) return;
        failureConsumed = true;
        throw new InvalidOperationException($"injected {point}");
    }

    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class FakeSwap(FakePlatform owner) : IInstallationSwap
    {
        private bool committed;
        public void Commit() { committed = true; owner.SwapCommitted = true; }
        public void Dispose() { if (!committed) owner.SwapRolledBack = true; }
    }
}
