namespace Winsomnia.Setup;

public sealed record SetupPaths(
    string Source,
    string SetupExecutable,
    string Target,
    string LegacyKillSwitch,
    string MarkerPath,
    string StatePath,
    string LegacyStatePath,
    string LegacyConfigPath,
    string StartMenuShortcut,
    string StartupShortcut)
{
    public static SetupPaths ForCurrentUser(string baseDirectory, string? processPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var stateDirectory = Path.Combine(localAppData, "winsomnia");
        return new(
            Path.Combine(baseDirectory, "app"),
            processPath ?? throw new InvalidOperationException("The setup executable path is unavailable."),
            Path.Combine(localAppData, "Programs", "winsomnia"),
            @"C:\temp\win-somnia-unlock.txt",
            @"C:\temp\winsomnia-lock-enabled.json",
            Path.Combine(stateDirectory, "state-v3.json"),
            Path.Combine(stateDirectory, "state-v2.json"),
            Path.Combine(stateDirectory, "config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "winsomnia.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "winsomnia.lnk"));
    }
}

public interface ISetupPlatform
{
    IDisposable AcquireSetupLease();
    void EnsureLegacyKillSwitch(string path);
    void DisableTask(string taskName);
    void EndTask(string taskName);
    void DeleteTask(string taskName);
    bool IsTaskDisabledOrMissing(string taskName);
    void AssertNoRuntimeProcesses(string installedRoot);
    void DisarmEngine(SetupPaths paths);
    void AssertEngineDisarmed(SetupPaths paths);
    string StageAndValidatePayload(SetupPaths paths);
    IInstallationSwap ReplaceInstallation(string stagedPath, string targetPath);
    void RegisterDisabledTask(ScheduledTaskPlan plan);
    void CreateShortcut(string path, string target, string arguments);
    void DeleteShortcut(string path);
    void DeleteInstallation(string targetPath);
}

public interface IInstallationSwap : IDisposable
{
    void Commit();
}

public sealed class SetupCoordinator(ISetupPlatform platform, SetupPaths paths)
{
    public static readonly string[] TaskNames = ["winsomnia", "win-somnia"];

    public void Install()
    {
        using var lease = platform.AcquireSetupLease();
        string? staged = null;
        IInstallationSwap? swap = null;
        try
        {
            EstablishSafetyBarrier();
            staged = platform.StageAndValidatePayload(paths);
            swap = platform.ReplaceInstallation(staged, paths.Target);
            staged = null;

            var engine = Path.Combine(paths.Target, "Winsomnia.Engine.exe");
            platform.RegisterDisabledTask(SetupTaskPlan.Define("winsomnia", engine,
                System.Security.Principal.WindowsIdentity.GetCurrent().Name));
            AssertSafePostconditions();

            platform.CreateShortcut(paths.StartMenuShortcut,
                Path.Combine(paths.Target, "Winsomnia.Desktop.exe"), "");
            platform.CreateShortcut(paths.StartupShortcut,
                Path.Combine(paths.Target, "Winsomnia.Desktop.exe"), "--tray");
            swap.Commit();
        }
        catch (Exception primary)
        {
            try { swap?.Dispose(); } catch { }
            try
            {
                EstablishSafetyBarrier();
            }
            catch (Exception barrier)
            {
                throw new AggregateException("Setup failed and the safety barrier could not be fully verified.",
                    primary, barrier);
            }
            throw new InvalidOperationException($"Setup stopped at a safe barrier: {primary.Message}", primary);
        }
        finally
        {
            swap?.Dispose();
            if (staged is not null && Directory.Exists(staged))
            {
                try { Directory.Delete(staged, true); } catch { }
            }
        }
    }

    public void Uninstall()
    {
        using var lease = platform.AcquireSetupLease();
        try
        {
            EstablishSafetyBarrier();
            foreach (var taskName in TaskNames) platform.DeleteTask(taskName);
            platform.DeleteShortcut(paths.StartMenuShortcut);
            platform.DeleteShortcut(paths.StartupShortcut);
            platform.DeleteInstallation(paths.Target);
        }
        catch (Exception primary)
        {
            try
            {
                EstablishSafetyBarrier();
            }
            catch (Exception barrier)
            {
                throw new AggregateException("Uninstall failed and the safety barrier could not be fully verified.",
                    primary, barrier);
            }
            throw new InvalidOperationException($"Uninstall stopped at a safe barrier: {primary.Message}", primary);
        }
    }

    private void EstablishSafetyBarrier()
    {
        var failures = new List<Exception>();
        Try(() => platform.EnsureLegacyKillSwitch(paths.LegacyKillSwitch), failures);
        foreach (var taskName in TaskNames) Try(() => platform.DisableTask(taskName), failures);
        foreach (var taskName in TaskNames) Try(() => platform.EndTask(taskName), failures);
        Try(() => platform.AssertNoRuntimeProcesses(paths.Target), failures);
        Try(() => platform.DisarmEngine(paths), failures);
        Try(() => platform.AssertEngineDisarmed(paths), failures);
        foreach (var taskName in TaskNames)
            Try(() =>
            {
                if (!platform.IsTaskDisabledOrMissing(taskName))
                    throw new InvalidOperationException($"Scheduled task '{taskName}' is not disabled.");
            }, failures);
        if (failures.Count > 0)
            throw new AggregateException("The safety barrier was not fully established.", failures);
    }

    private void AssertSafePostconditions()
    {
        platform.EnsureLegacyKillSwitch(paths.LegacyKillSwitch);
        platform.AssertNoRuntimeProcesses(paths.Target);
        platform.AssertEngineDisarmed(paths);
        foreach (var taskName in TaskNames)
        {
            if (!platform.IsTaskDisabledOrMissing(taskName))
                throw new InvalidOperationException($"Scheduled task '{taskName}' is not disabled.");
        }
    }

    private static void Try(Action action, ICollection<Exception> failures)
    {
        try { action(); }
        catch (Exception exception) { failures.Add(exception); }
    }
}

public sealed record ScheduledTaskPlan(string TaskName, string EnginePath, string UserId, string Description,
    bool AllowStartOnBattery, bool StopOnBattery, int RestartCount, bool Enabled);

public static class SetupTaskPlan
{
    public static ScheduledTaskPlan Define(string taskName, string enginePath, string userId)
    {
        if (!Path.IsPathFullyQualified(enginePath))
            throw new ArgumentException("The engine path must be absolute.", nameof(enginePath));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("The current user identity is required.", nameof(userId));
        return new(taskName, enginePath, userId,
            "Repeatedly locks the workstation during configured restricted hours.",
            AllowStartOnBattery: true, StopOnBattery: false, RestartCount: 3, Enabled: false);
    }
}
