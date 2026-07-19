using System.Text;
using Winsomnia.Setup;

TestTaskDefinition();
TestSuccessfulCutoverOrder();
foreach (var failure in new[]
{
    "Switch", "Disable:winsomnia", "End:winsomnia", "AssertProcesses", "Disarm", "Marker", "AssertDisarmed",
    "Recover", "Stage", "Swap", "Register", "PostTaskQuery", "Shortcut"
}) TestFailureReturnsToBarrier(failure);
TestV2MigrationIsReadOnlyAndDisarmed();
TestStateFailureStillRevokesMarker();
TestPayloadStagingAndValidation();
TestAtomicSwapRollbackAndCommit();
TestSetupLeaseRejectsConcurrentOwner();
TestEngineLeaseRejectsConcurrentSafetyMutation();
TestDurableRecoveryPhases();
TestDurableRecoveryFailures();
TestSafetyDecisionLogic();
TestMarkerDirectoryRemainsNonAuthorizing();
TestUninstallSafety();
TestPersistentPreMutationFailureIsNotReportedSafe();
TestPersistentMarkerFailureRemainsDisarmed();

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
        "AssertProcesses", "Disarm", "AssertDisarmed", "TaskQuery:winsomnia", "TaskQuery:win-somnia", "Recover", "Stage", "Swap",
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
        var framework = "net10.0-windows10.0.22000.0";
        var engine = Path.GetFullPath(Path.Combine("src", "Winsomnia.Engine", "bin", "Debug", framework,
            "Winsomnia.Engine.exe"));
        var desktop = Path.GetFullPath(Path.Combine("src", "Winsomnia.Desktop", "bin", "Debug", framework,
            "Winsomnia.Desktop.exe"));
        var setup = Path.GetFullPath(Path.Combine("src", "Winsomnia.Setup", "bin", "Debug", framework,
            "Winsomnia.Setup.exe"));
        File.Copy(engine, Path.Combine(source, "Winsomnia.Engine.exe"));
        File.Copy(desktop, Path.Combine(source, "Winsomnia.Desktop.exe"));
        File.WriteAllText(Path.Combine(source, "VERSION"), "0.3.0");
        var paths = Paths(root) with { Source = source, SetupExecutable = setup, Target = Path.Combine(root, "installed") };
        var platform = new WindowsSetupPlatform();
        var stage = platform.StageAndValidatePayload(paths);
        Assert(stage == UpgradeLocations.ForTarget(paths.Target).Stage, "Staging path is not deterministic.");
        Assert(File.Exists(Path.Combine(stage, "Winsomnia.Setup.exe")) && File.Exists(Path.Combine(stage, "VERSION")),
            "Validated staging omitted required payload files.");
        Directory.Delete(stage, true);

        File.WriteAllText(Path.Combine(source, "Winsomnia.Engine.exe"), "not a PE image");
        AssertThrows(() => platform.StageAndValidatePayload(paths), "A text file passed executable validation.");
        Assert(!Directory.Exists(UpgradeLocations.ForTarget(paths.Target).Stage),
            "Failed payload validation left the deterministic staging directory behind.");
        var cleanupFault = new FaultingUpgradeFileSystem { FailOnce = "Delete:.winsomnia-upgrade-stage" };
        var faultingPlatform = new WindowsSetupPlatform(cleanupFault);
        AssertThrows(() => faultingPlatform.StageAndValidatePayload(paths),
            "Staging cleanup failure was reported as a normal validation failure.");
        Assert(Directory.Exists(UpgradeLocations.ForTarget(paths.Target).Stage),
            "Injected staging cleanup failure did not preserve retry evidence.");
        faultingPlatform.RecoverInstallation(paths.Target);
        Assert(!Directory.Exists(UpgradeLocations.ForTarget(paths.Target).Stage),
            "Startup recovery did not retry staging cleanup.");
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
        var stage = UpgradeLocations.ForTarget(target).Stage;
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
        using (new WindowsSetupPlatform().ReplaceInstallation(stage, target)) { }
        Assert(File.Exists(Path.Combine(target, "old.txt")) && !File.Exists(Path.Combine(target, "new.txt")),
            "Interrupted replacement did not restore the old installation.");

        stage = UpgradeLocations.ForTarget(target).Stage;
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
static void TestDurableRecoveryPhases()
{
    foreach (var scenario in new[] { "Prepared", "PreparedAfterMove", "OldMoved", "OldMovedAfterNew", "NewMoved", "Committed" })
    {
        var root = Path.Combine(Path.GetTempPath(), $"winsomnia-recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "installed");
            var locations = UpgradeLocations.ForTarget(target);
            WriteTag(target, "old");
            WriteTag(locations.Stage, "new");
            var recovery = new DurableInstallationRecovery(new UpgradeFileSystem());
            if (scenario == "Prepared") recovery.Save(locations, UpgradePhases.Prepared);
            else
            {
                Directory.Move(target, locations.Backup);
                recovery.Save(locations, scenario == "PreparedAfterMove" ? UpgradePhases.Prepared : UpgradePhases.OldMoved);
                if (scenario is "OldMovedAfterNew" or "NewMoved" or "Committed")
                {
                    Directory.Move(locations.Stage, target);
                    recovery.Save(locations, scenario == "Committed" ? UpgradePhases.Committed :
                        scenario == "NewMoved" ? UpgradePhases.NewMoved : UpgradePhases.OldMoved);
                }
            }
            recovery.Recover(target);
            var expected = scenario == "Committed" ? "new" : "old";
            Assert(ReadTag(target) == expected, $"{scenario} recovery selected the wrong installation.");
            Assert(!Directory.Exists(locations.Stage) && !Directory.Exists(locations.Backup) && !File.Exists(locations.Journal),
                $"{scenario} recovery left upgrade artifacts.");
        }
        finally { Directory.Delete(root, true); }
    }
}

static void TestDurableRecoveryFailures()
{
    TestSwapFailure("Move:installed", commit: false, expected: "old");
    TestSwapFailure("Move:.winsomnia-upgrade-stage", commit: false, expected: "old");
    TestSwapFailure("Write:Committed", commit: true, expected: "old");

    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-commit-cleanup-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var target = Path.Combine(root, "installed");
        var locations = UpgradeLocations.ForTarget(target);
        WriteTag(target, "old");
        WriteTag(locations.Stage, "new");
        var fault = new FaultingUpgradeFileSystem { FailOnce = "Delete:.winsomnia-upgrade-backup" };
        var swap = new DurableInstallationSwap(fault, locations.Stage, target);
        AssertThrows(swap.Commit, "Backup cleanup failure was ignored.");
        new DurableInstallationRecovery(fault).Recover(target);
        Assert(ReadTag(target) == "new", "Committed cleanup failure restored the old installation.");
        swap.Dispose();
    }
    finally { Directory.Delete(root, true); }
    root = Path.Combine(Path.GetTempPath(), $"winsomnia-rollback-delete-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var target = Path.Combine(root, "installed");
        var locations = UpgradeLocations.ForTarget(target);
        WriteTag(target, "old");
        WriteTag(locations.Stage, "new");
        var fault = new FaultingUpgradeFileSystem { FailAlways = "Delete:installed" };
        var swap = new DurableInstallationSwap(fault, locations.Stage, target);
        AssertThrows(swap.Dispose, "Persistent rollback delete failure was ignored.");
        Assert(File.Exists(locations.Journal), "Rollback delete failure discarded its journal.");
        fault.FailAlways = null;
        new DurableInstallationRecovery(fault).Recover(target);
        Assert(ReadTag(target) == "old", "Rollback retry did not restore the old installation.");
    }
    finally { Directory.Delete(root, true); }

    root = Path.Combine(Path.GetTempPath(), $"winsomnia-cleanup-retry-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var target = Path.Combine(root, "installed");
        var locations = UpgradeLocations.ForTarget(target);
        WriteTag(target, "old");
        WriteTag(locations.Stage, "new");
        var recovery = new DurableInstallationRecovery(new UpgradeFileSystem());
        recovery.Save(locations, UpgradePhases.Prepared);
        var fault = new FaultingUpgradeFileSystem { FailAlways = "Delete:.winsomnia-upgrade-stage" };
        AssertThrows(() => new DurableInstallationRecovery(fault).Recover(target), "Persistent stage cleanup failure was ignored.");
        Assert(File.Exists(locations.Journal), "Cleanup failure discarded the recovery journal.");
        fault.FailAlways = null;
        new DurableInstallationRecovery(fault).Recover(target);
        Assert(ReadTag(target) == "old" && !Directory.Exists(locations.Stage), "Cleanup retry did not finish safely.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestSwapFailure(string failure, bool commit, string expected)
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-swap-failure-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var target = Path.Combine(root, "installed");
        var locations = UpgradeLocations.ForTarget(target);
        WriteTag(target, "old");
        WriteTag(locations.Stage, "new");
        var fault = new FaultingUpgradeFileSystem { FailOnce = failure };
        if (!commit)
            AssertThrows(() => new DurableInstallationSwap(fault, locations.Stage, target), $"{failure} was ignored.");
        else
        {
            using var swap = new DurableInstallationSwap(fault, locations.Stage, target);
            AssertThrows(swap.Commit, $"{failure} was ignored.");
        }
        new DurableInstallationRecovery(fault).Recover(target);
        Assert(ReadTag(target) == expected, $"{failure} recovery selected the wrong installation.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestSafetyDecisionLogic()
{
    Assert(TaskStateDecision.MustStop(TaskStateDecision.Queued), "Queued task was not stopped.");
    Assert(TaskStateDecision.MustStop(TaskStateDecision.Running), "Running task was not stopped.");
    Assert(!TaskStateDecision.IsSafelyDisabled(false, TaskStateDecision.Queued), "Queued task was called safe.");
    Assert(TaskStateDecision.IsSafelyDisabled(false, 3), "Disabled ready task was rejected.");
    var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "installed"));
    Assert(RuntimeProcessDecision.IsInstalledRuntime("Winsomnia.Engine", Path.Combine(root, "Winsomnia.Engine.exe"), null, root),
        "Exact installed Engine path was missed.");
    Assert(!RuntimeProcessDecision.IsInstalledRuntime("Winsomnia.Engine", Path.Combine(Path.GetTempPath(), "other", "Winsomnia.Engine.exe"), null, root),
        "Unrelated Engine path was treated as installed.");
    Assert(RuntimeProcessDecision.IsInstalledRuntime("powershell.exe", null,
        $"powershell -File \"{Path.Combine(root, "winsomnia-monitor.ps1")}\"", root), "Exact monitor path was missed.");
    Assert(SetupPathPolicy.IsInsideTarget(Path.Combine(root, "Winsomnia.Setup.exe"), root), "Installed setup path was not detected.");
    Assert(!SetupPathPolicy.IsInsideTarget(Path.Combine(Path.GetTempPath(), "package", "Winsomnia.Setup.exe"), root),
        "External setup path was rejected.");
}

static void TestMarkerDirectoryRemainsNonAuthorizing()
{
    var root = Path.Combine(Path.GetTempPath(), $"winsomnia-marker-directory-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    try
    {
        var paths = Paths(root);
        Directory.CreateDirectory(paths.MarkerPath);
        File.WriteAllText(Path.Combine(paths.MarkerPath, "do-not-delete.txt"), "sentinel");
        AssertThrows(() => new WindowsSetupPlatform().DisarmEngine(paths), "Marker directory unexpectedly passed absence enforcement.");
        var state = new Winsomnia.Core.StateManager(paths.StatePath, new Winsomnia.Core.SystemClock()).LoadOrCreate();
        Assert(!state.Armed && state.ActivationId is null, "Marker failure did not persist disarmed state.");
        Assert(File.Exists(Path.Combine(paths.MarkerPath, "do-not-delete.txt")), "Marker directory contents were recursively deleted.");
        var authorization = new Winsomnia.Engine.LockMarkerStore(paths.MarkerPath).Inspect(state, true);
        Assert(authorization.State == Winsomnia.Core.LockAuthorizationStates.Disarmed,
            "Restart with marker directory was authorizing.");
    }
    finally { Directory.Delete(root, true); }
}

static void TestUninstallSafety()
{
    var success = new FakePlatform();
    new SetupCoordinator(success, Paths()).Uninstall();
    Assert(success.Events.Contains("AssertExternal") && success.Events.Contains("DeleteInstallation"),
        "External uninstall did not reach deletion after the barrier.");

    foreach (var failure in new[] { "AssertExternal", "Delete:winsomnia", "Delete:win-somnia", "DeleteInstallation" })
    {
        var fake = new FakePlatform { FailOnce = failure };
        AssertThrows(() => new SetupCoordinator(fake, Paths()).Uninstall(), $"Uninstall failure {failure} was ignored.");
        Assert(fake.BarrierRuns >= 2 && fake.Tasks.Values.All(value => !value) && !fake.Armed && !fake.Marker,
            $"Uninstall failure {failure} did not rerun the safety barrier.");
    }
}

static void TestPersistentPreMutationFailureIsNotReportedSafe()
{
    foreach (var task in SetupCoordinator.TaskNames)
    {
        var fake = new FakePlatform { FailAlways = $"Disable:{task}", FailBeforeMutation = true };
        AssertThrows(() => new SetupCoordinator(fake, Paths()).Install(),
            $"Persistent task control failure for {task} was reported as success.");
        Assert(fake.Tasks[task], $"Pre-mutation failure for {task} unexpectedly changed fake state.");
        Assert(!fake.SwapCommitted, $"Persistent task failure for {task} reached commit.");
    }
}

static void TestPersistentMarkerFailureRemainsDisarmed()
{
    var fake = new FakePlatform { FailAlways = "Marker" };
    AssertThrows(() => new SetupCoordinator(fake, Paths()).Install(),
        "Persistent marker removal failure was reported as success.");
    Assert(!fake.Armed && fake.Marker, "Marker failure fake did not model disarmed-but-present state.");
    Assert(!fake.SwapCommitted && fake.Tasks.Values.All(value => !value),
        "Persistent marker failure reached installation commit or left tasks enabled.");
}
static void WriteTag(string directory, string value)
{
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "tag.txt"), value);
}

static string ReadTag(string directory) => File.ReadAllText(Path.Combine(directory, "tag.txt"));
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
    public string? FailAlways { get; init; }
    public bool FailBeforeMutation { get; init; }
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
        FailBefore("Switch");
        KillSwitch = true;
        Fail("Switch");
    }
    public void DisableTask(string taskName)
    {
        Events.Add($"Disable:{taskName}");
        FailBefore($"Disable:{taskName}");
        Tasks[taskName] = false;
        Fail($"Disable:{taskName}");
    }
    public void EndTask(string taskName)
    {
        Events.Add($"End:{taskName}");
        FailBefore($"End:{taskName}");
        if (taskName == "win-somnia") ProcessRunning = false;
        Fail($"End:{taskName}");
    }
    public void DeleteTask(string taskName)
    {
        Events.Add($"Delete:{taskName}");
        FailBefore($"Delete:{taskName}");
        Tasks.Remove(taskName);
        Fail($"Delete:{taskName}");
    }
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
        FailBefore("Disarm");
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
    public void RecoverInstallation(string targetPath) { Events.Add("Recover"); Fail("Recover"); }
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
    public void AssertExternalSetup(string setupExecutable, string targetPath) { Events.Add("AssertExternal"); Fail("AssertExternal"); }
    public void DeleteInstallation(string targetPath) { Events.Add("DeleteInstallation"); Fail("DeleteInstallation"); }

    private void FailBefore(string point)
    {
        if (FailBeforeMutation) Fail(point);
    }

    private void Fail(string point)
    {
        if (FailAlways == point) throw new InvalidOperationException($"persistent {point}");
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
sealed class FaultingUpgradeFileSystem : IUpgradeFileSystem
{
    private readonly UpgradeFileSystem inner = new();
    private bool consumed;
    public string? FailOnce { get; init; }
    public string? FailAlways { get; set; }

    public bool DirectoryExists(string path) => inner.DirectoryExists(path);
    public bool FileExists(string path) => inner.FileExists(path);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public string ReadAllText(string path) => inner.ReadAllText(path);
    public void DeleteFile(string path) { Fail($"DeleteFile:{Path.GetFileName(path)}"); inner.DeleteFile(path); }
    public void DeleteDirectory(string path) { Fail($"Delete:{Path.GetFileName(path)}"); inner.DeleteDirectory(path); }
    public void MoveDirectory(string source, string destination)
    {
        Fail($"Move:{Path.GetFileName(source)}");
        inner.MoveDirectory(source, destination);
    }
    public void WriteAllTextAtomic(string path, string content)
    {
        var phase = new[] { UpgradePhases.Prepared, UpgradePhases.OldMoved, UpgradePhases.NewMoved, UpgradePhases.Committed }
            .FirstOrDefault(content.Contains) ?? "Unknown";
        Fail($"Write:{phase}");
        inner.WriteAllTextAtomic(path, content);
    }

    private void Fail(string point)
    {
        if (FailAlways == point) throw new IOException($"persistent {point}");
        if (!consumed && FailOnce == point)
        {
            consumed = true;
            throw new IOException($"injected {point}");
        }
    }
}
