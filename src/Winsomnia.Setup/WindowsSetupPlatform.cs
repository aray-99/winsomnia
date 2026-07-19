using System.Diagnostics;
using System.Runtime.InteropServices;
using Winsomnia.Core;
using Winsomnia.Engine;

namespace Winsomnia.Setup;

public sealed class WindowsSetupPlatform : ISetupPlatform
{
    private readonly IUpgradeFileSystem upgradeFiles;
    private readonly TimeSpan verificationTimeout;
    private readonly Action<TimeSpan> delay;

    public WindowsSetupPlatform(IUpgradeFileSystem? upgradeFiles = null, TimeSpan? verificationTimeout = null,
        Action<TimeSpan>? delay = null)
    {
        this.upgradeFiles = upgradeFiles ?? new UpgradeFileSystem();
        this.verificationTimeout = verificationTimeout ?? TimeSpan.FromSeconds(5);
        this.delay = delay ?? Thread.Sleep;
    }

    public IDisposable AcquireSetupLease() => new NamedMutexLease(@"Local\winsomnia.setup.v3");

    public void EnsureLegacyKillSwitch(string path)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidDataException("The legacy kill switch path has no parent.");
        Directory.CreateDirectory(parent);
        if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, string.Empty);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new IOException("The legacy kill switch could not be verified.");
    }

    public void DisableTask(string taskName)
    {
        dynamic? task = FindTask(taskName);
        if (task is null) return;
        task.Enabled = false;
        WaitForTask(taskName, snapshot => !snapshot.Enabled, "disabled");
    }

    public void EndTask(string taskName)
    {
        dynamic? task = FindTask(taskName);
        if (task is null) return;
        if (TaskStateDecision.MustStop((int)task.State)) task.Stop(0);
        WaitForTask(taskName,
            snapshot => TaskStateDecision.IsSafelyDisabled(snapshot.Enabled, snapshot.State),
            "disabled and stopped");
    }

    public void DeleteTask(string taskName)
    {
        if (FindTask(taskName) is null) return;
        dynamic folder = GetTaskFolder();
        folder.DeleteTask(taskName, 0);
        WaitForTask(taskName, _ => false, "absent");
    }

    public bool IsTaskDisabledOrMissing(string taskName)
    {
        var snapshot = ReadTaskSnapshot(taskName);
        return snapshot is null || TaskStateDecision.IsSafelyDisabled(snapshot.Enabled, snapshot.State);
    }

    public void AssertNoRuntimeProcesses(string installedRoot)
    {
        var deadline = DateTime.UtcNow + verificationTimeout;
        while (true)
        {
            var matches = FindInstalledRuntimeProcesses(installedRoot);
            if (matches.Count == 0) return;
            if (DateTime.UtcNow >= deadline)
                throw new InvalidOperationException("Installed winsomnia runtime is still running: " +
                    string.Join(", ", matches));
            delay(TimeSpan.FromMilliseconds(100));
        }
    }

    public void DisarmEngine(SetupPaths paths)
    {
        using var lease = new NamedMutexLease(EngineHost.GetMutexName());
        var manager = new StateManager(paths.StatePath, new SystemClock());
        var marker = new LockMarkerStore(paths.MarkerPath);
        try
        {
            OfflineSafety.Pause(manager, marker, paths.LegacyStatePath, paths.LegacyConfigPath);
        }
        catch
        {
            try { marker.Revoke(); } catch { }
            throw;
        }
    }

    public void AssertEngineDisarmed(SetupPaths paths)
    {
        var manager = new StateManager(paths.StatePath, new SystemClock());
        var state = manager.LoadOrCreate(paths.LegacyStatePath, paths.LegacyConfigPath);
        if (state.Armed || state.ActivationId is not null)
            throw new InvalidOperationException("Engine state is not disarmed.");
        var authorization = new LockMarkerStore(paths.MarkerPath).Inspect(state, realLockEnabled: true);
        if (authorization.State != LockAuthorizationStates.Disarmed)
            throw new InvalidOperationException("Engine lock authorization is not disarmed.");
        if (File.Exists(paths.MarkerPath) || Directory.Exists(paths.MarkerPath))
            throw new InvalidOperationException("The non-authorizing v3 lock marker could not be made absent.");
    }

    public void RecoverInstallation(string targetPath) =>
        new DurableInstallationRecovery(upgradeFiles).Recover(targetPath);

    public string StageAndValidatePayload(SetupPaths paths)
    {
        if (!Directory.Exists(paths.Source))
            throw new DirectoryNotFoundException($"Installer payload was not found: {paths.Source}");
        var locations = UpgradeLocations.ForTarget(paths.Target);
        upgradeFiles.CreateDirectory(Path.GetDirectoryName(locations.Target)!);
        if (upgradeFiles.DirectoryExists(locations.Stage))
            throw new InvalidOperationException("Upgrade staging cleanup has not completed.");
        try
        {
            CopyDirectorySafe(paths.Source, locations.Stage);
            File.Copy(paths.SetupExecutable, Path.Combine(locations.Stage, "Winsomnia.Setup.exe"), true);
            PayloadValidator.Validate(locations.Stage);
            return locations.Stage;
        }
        catch (Exception primary)
        {
            try
            {
                if (upgradeFiles.DirectoryExists(locations.Stage)) upgradeFiles.DeleteDirectory(locations.Stage);
            }
            catch (Exception cleanup)
            {
                throw new AggregateException("Payload staging and cleanup both failed.", primary, cleanup);
            }
            throw;
        }
    }

    public IInstallationSwap ReplaceInstallation(string stagedPath, string targetPath) =>
        new DurableInstallationSwap(upgradeFiles, stagedPath, targetPath);

    public void RegisterDisabledTask(ScheduledTaskPlan plan)
    {
        if (plan.Enabled) throw new InvalidOperationException("Setup refuses to register an enabled task.");
        dynamic service = CreateTaskService();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = plan.Description;
        definition.Principal.UserId = plan.UserId;
        definition.Principal.LogonType = 3;
        definition.Principal.RunLevel = 0;
        definition.Settings.Enabled = false;
        definition.Settings.DisallowStartIfOnBatteries = !plan.AllowStartOnBattery;
        definition.Settings.StopIfGoingOnBatteries = plan.StopOnBattery;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2;
        definition.Settings.RestartCount = plan.RestartCount;
        definition.Settings.RestartInterval = "PT1M";
        dynamic trigger = definition.Triggers.Create(9);
        trigger.UserId = plan.UserId;
        dynamic action = definition.Actions.Create(0);
        action.Path = plan.EnginePath;
        action.Arguments = "--enable-lock";
        folder.RegisterTaskDefinition(plan.TaskName, definition, 6, null, null, 3, null);
        WaitForTask(plan.TaskName,
            snapshot => TaskStateDecision.IsSafelyDisabled(snapshot.Enabled, snapshot.State),
            "registered disabled and stopped", requirePresent: true);
    }

    public void CreateShortcut(string path, string target, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.Arguments = arguments;
        shortcut.WorkingDirectory = Path.GetDirectoryName(target);
        shortcut.Save();
    }

    public void DeleteShortcut(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void AssertExternalSetup(string setupExecutable, string targetPath)
    {
        if (SetupPathPolicy.IsInsideTarget(setupExecutable, targetPath))
            throw new InvalidOperationException(
                "Uninstall must be run from an extracted release package outside the installation directory.");
    }

    public void DeleteInstallation(string targetPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programsRoot = Path.GetFullPath(Path.Combine(localAppData, "Programs")) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(targetPath);
        if (!fullTarget.StartsWith(programsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove an unexpected installation path.");
        if (Directory.Exists(fullTarget)) new UpgradeFileSystem().DeleteDirectory(fullTarget);
    }

    private void WaitForTask(string taskName, Func<TaskSnapshot, bool> predicate, string expected,
        bool requirePresent = false)
    {
        var deadline = DateTime.UtcNow + verificationTimeout;
        while (true)
        {
            var snapshot = ReadTaskSnapshot(taskName);
            if (snapshot is null ? !requirePresent : predicate(snapshot)) return;
            if (DateTime.UtcNow >= deadline)
                throw new TaskControlUnverifiedException(taskName, expected);
            delay(TimeSpan.FromMilliseconds(100));
        }
    }

    private static TaskSnapshot? ReadTaskSnapshot(string taskName)
    {
        dynamic? task = FindTask(taskName);
        return task is null ? null : new TaskSnapshot((bool)task.Enabled, (int)task.State);
    }

    private static IReadOnlyList<string> FindInstalledRuntimeProcesses(string installedRoot)
    {
        var matches = new List<string>();
        foreach (var process in Process.GetProcessesByName("Winsomnia.Engine"))
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    throw new InvalidOperationException("Could not verify a Winsomnia.Engine process path.", exception);
                }
                if (RuntimeProcessDecision.IsInstalledRuntime(process.ProcessName, path, null, installedRoot))
                    matches.Add($"Winsomnia.Engine ({process.Id})");
            }
        }

        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
                ?? throw new InvalidOperationException("Windows process inspection is unavailable.");
            dynamic locator = Activator.CreateInstance(locatorType)!;
            dynamic service = locator.ConnectServer(".", @"root\cimv2");
            dynamic processes = service.ExecQuery(
                "SELECT ProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe'");
            foreach (dynamic process in processes)
            {
                if (RuntimeProcessDecision.IsInstalledRuntime((string)process.Name,
                        process.ExecutablePath as string, process.CommandLine as string, installedRoot))
                    matches.Add($"{process.Name} ({process.ProcessId}) running the installed monitor");
            }
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("Could not verify legacy monitor processes.", exception);
        }
        return matches;
    }

    private static dynamic CreateTaskService()
    {
        var serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(serviceType)!;
        service.Connect();
        return service;
    }

    private static dynamic GetTaskFolder() => CreateTaskService().GetFolder("\\");

    private static dynamic? FindTask(string taskName)
    {
        dynamic folder = GetTaskFolder();
        dynamic tasks = folder.GetTasks(1);
        foreach (dynamic task in tasks)
        {
            if (((string)task.Name).Equals(taskName, StringComparison.OrdinalIgnoreCase)) return task;
        }
        return null;
    }

    private static void CopyDirectorySafe(string source, string destination)
    {
        var sourceInfo = new DirectoryInfo(source);
        if ((sourceInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installer payload root cannot be a reparse point.");
        Directory.CreateDirectory(destination);
        foreach (var file in sourceInfo.GetFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Installer payload contains a reparse point: {file.Name}");
            file.CopyTo(Path.Combine(destination, file.Name), true);
        }
        foreach (var directory in sourceInfo.GetDirectories())
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Installer payload contains a reparse point: {directory.Name}");
            CopyDirectorySafe(directory.FullName, Path.Combine(destination, directory.Name));
        }
    }
}

public sealed record TaskSnapshot(bool Enabled, int State);

internal sealed class NamedMutexLease : IDisposable
{
    private readonly Mutex mutex;
    private bool owns;

    public NamedMutexLease(string name)
    {
        mutex = new Mutex(false, name);
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Another winsomnia setup or Engine instance is running.");
        }
    }

    public void Dispose()
    {
        if (owns)
        {
            mutex.ReleaseMutex();
            owns = false;
        }
        mutex.Dispose();
    }
}
