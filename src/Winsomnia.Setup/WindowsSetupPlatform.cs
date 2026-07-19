using System.Diagnostics;
using System.Runtime.InteropServices;
using Winsomnia.Core;
using Winsomnia.Engine;

namespace Winsomnia.Setup;

public sealed class WindowsSetupPlatform : ISetupPlatform
{
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
        if ((bool)task.Enabled)
            throw new InvalidOperationException($"Scheduled task '{taskName}' remained enabled.");
    }

    public void EndTask(string taskName)
    {
        dynamic? task = FindTask(taskName);
        if (task is null) return;
        if ((int)task.State == 4) task.Stop(0); // TASK_STATE_RUNNING
        task = FindTask(taskName);
        if (task is not null && (int)task.State == 4)
            throw new InvalidOperationException($"Scheduled task '{taskName}' remained running.");
    }

    public void DeleteTask(string taskName)
    {
        if (FindTask(taskName) is null) return;
        dynamic folder = GetTaskFolder();
        folder.DeleteTask(taskName, 0);
        if (FindTask(taskName) is not null)
            throw new InvalidOperationException($"Scheduled task '{taskName}' was not deleted.");
    }

    public bool IsTaskDisabledOrMissing(string taskName)
    {
        dynamic? task = FindTask(taskName);
        return task is null || !(bool)task.Enabled;
    }

    public void AssertNoRuntimeProcesses(string installedRoot)
    {
        var matches = new List<string>();
        foreach (var process in Process.GetProcessesByName("Winsomnia.Engine"))
        {
            using (process) matches.Add($"Winsomnia.Engine ({process.Id})");
        }

        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
                ?? throw new InvalidOperationException("Windows process inspection is unavailable.");
            {
                dynamic locator = Activator.CreateInstance(locatorType)!;
                dynamic service = locator.ConnectServer(".", @"root\cimv2");
                dynamic processes = service.ExecQuery(
                    "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe'");
                foreach (dynamic process in processes)
                {
                    string? commandLine = process.CommandLine as string;
                    if (commandLine?.Contains("winsomnia-monitor.ps1", StringComparison.OrdinalIgnoreCase) == true)
                        matches.Add($"{process.Name} ({process.ProcessId}) running winsomnia-monitor.ps1");
                }
            }
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException("Could not verify legacy monitor processes.", exception);
        }

        if (matches.Count > 0)
            throw new InvalidOperationException("Installed winsomnia runtime is still running: " + string.Join(", ", matches));
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
        if (File.Exists(paths.MarkerPath) || Directory.Exists(paths.MarkerPath))
            throw new InvalidOperationException("The v3 lock marker is still present.");
        var authorization = new LockMarkerStore(paths.MarkerPath).Inspect(state, realLockEnabled: true);
        if (authorization.State != LockAuthorizationStates.Disarmed)
            throw new InvalidOperationException("Engine lock authorization is not disarmed.");
    }

    public string StageAndValidatePayload(SetupPaths paths)
    {
        if (!Directory.Exists(paths.Source))
            throw new DirectoryNotFoundException($"Installer payload was not found: {paths.Source}");
        var parent = Path.GetDirectoryName(Path.GetFullPath(paths.Target))
            ?? throw new InvalidDataException("The installation path has no parent.");
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, $".winsomnia-stage-{Guid.NewGuid():N}");
        try
        {
            CopyDirectorySafe(paths.Source, stage);
            File.Copy(paths.SetupExecutable, Path.Combine(stage, "Winsomnia.Setup.exe"), true);
            ValidatePayload(stage);
            return stage;
        }
        catch
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            throw;
        }
    }

    public IInstallationSwap ReplaceInstallation(string stagedPath, string targetPath) =>
        new AtomicDirectorySwap(stagedPath, targetPath);

    public void RegisterDisabledTask(ScheduledTaskPlan plan)
    {
        dynamic service = CreateTaskService();
        dynamic folder = service.GetFolder("\\");
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = plan.Description;
        definition.Principal.UserId = plan.UserId;
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
        definition.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
        definition.Settings.Enabled = false;
        definition.Settings.DisallowStartIfOnBatteries = !plan.AllowStartOnBattery;
        definition.Settings.StopIfGoingOnBatteries = plan.StopOnBattery;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
        definition.Settings.RestartCount = plan.RestartCount;
        definition.Settings.RestartInterval = "PT1M";
        dynamic trigger = definition.Triggers.Create(9); // TASK_TRIGGER_LOGON
        trigger.UserId = plan.UserId;
        dynamic action = definition.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = plan.EnginePath;
        action.Arguments = "--enable-lock";
        folder.RegisterTaskDefinition(plan.TaskName, definition, 6, null, null, 3, null);
        if (!IsTaskDisabledOrMissing(plan.TaskName) || FindTask(plan.TaskName) is null)
            throw new InvalidOperationException("The installed task was not registered disabled.");
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

    public void DeleteInstallation(string targetPath)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programsRoot = Path.GetFullPath(Path.Combine(localAppData, "Programs")) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(targetPath);
        if (!fullTarget.StartsWith(programsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove an unexpected installation path.");
        if (Directory.Exists(fullTarget)) Directory.Delete(fullTarget, true);
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
        dynamic tasks = folder.GetTasks(1); // include hidden tasks
        foreach (dynamic task in tasks)
        {
            if (((string)task.Name).Equals(taskName, StringComparison.OrdinalIgnoreCase)) return task;
        }
        return null;
    }

    private static void ValidatePayload(string root)
    {
        foreach (var file in new[] { "Winsomnia.Engine.exe", "Winsomnia.Desktop.exe", "Winsomnia.Setup.exe", "VERSION" })
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                throw new InvalidDataException($"Installer payload is missing or empty: {file}");
        }
        var version = File.ReadAllText(Path.Combine(root, "VERSION")).Trim();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Installer VERSION is empty.");
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

internal sealed class AtomicDirectorySwap : IInstallationSwap
{
    private readonly string target;
    private readonly string? backup;
    private bool committed;
    private bool disposed;

    public AtomicDirectorySwap(string stagedPath, string targetPath)
    {
        target = Path.GetFullPath(targetPath);
        var stage = Path.GetFullPath(stagedPath);
        if (!Directory.Exists(stage)) throw new DirectoryNotFoundException("The staged installation is missing.");
        if (!string.Equals(Path.GetDirectoryName(stage), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staged payload must be beside the installation target.");
        backup = Directory.Exists(target)
            ? Path.Combine(Path.GetDirectoryName(target)!, $".winsomnia-backup-{Guid.NewGuid():N}")
            : null;
        if (backup is not null) Directory.Move(target, backup);
        try { Directory.Move(stage, target); }
        catch
        {
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
                Directory.Move(backup, target);
            throw;
        }
    }

    public void Commit()
    {
        if (disposed) throw new ObjectDisposedException(nameof(AtomicDirectorySwap));
        if (backup is not null && Directory.Exists(backup)) Directory.Delete(backup, true);
        committed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        if (!committed)
        {
            if (Directory.Exists(target)) Directory.Delete(target, true);
            if (backup is not null && Directory.Exists(backup)) Directory.Move(backup, target);
        }
        disposed = true;
    }
}
