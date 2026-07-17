using System.Diagnostics;
using System.Security.Principal;

const string TaskName = "winsomnia";
var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "install";
var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var target = Path.Combine(localAppData, "Programs", "winsomnia");
var killSwitch = @"C:\temp\win-somnia-unlock.txt";

try
{
    if (command == "uninstall")
    {
        EnsureKillSwitch(killSwitch);
        Run(SetupTaskPlan.End(TaskName));
        Run(SetupTaskPlan.Delete(TaskName));
        DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "winsomnia.lnk"));
        DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "winsomnia.lnk"));
        var programsRoot = Path.GetFullPath(Path.Combine(localAppData, "Programs")) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(programsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove an unexpected installation path.");
        if (Directory.Exists(fullTarget)) Directory.Delete(fullTarget, true);
        Console.WriteLine("winsomnia was removed. The kill switch and user data were retained.");
        return 0;
    }

    var source = Path.Combine(AppContext.BaseDirectory, "app");
    if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Installer payload was not found: {source}");
    EnsureKillSwitch(killSwitch);
    Run(SetupTaskPlan.End(TaskName));
    CopyDirectory(source, target);
    if (Environment.ProcessPath is not null)
        File.Copy(Environment.ProcessPath, Path.Combine(target, "Winsomnia.Setup.exe"), true);
    var engine = Path.Combine(target, "Winsomnia.Engine.exe");
    var desktop = Path.Combine(target, "Winsomnia.Desktop.exe");
    RegisterTask(SetupTaskPlan.Define(TaskName, engine, WindowsIdentity.GetCurrent().Name));
    CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "winsomnia.lnk"), desktop, "");
    CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "winsomnia.lnk"), desktop, "--tray");
    Console.WriteLine("winsomnia was installed for the current user and remains paused.");
    Console.WriteLine("Open winsomnia from the Start menu to complete setup.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"winsomnia setup failed safely: {exception.Message}");
    return 1;
}

static void EnsureKillSwitch(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, string.Empty);
}
static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
    foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
}
static void Run(ProcessCommand command)
{
    var startInfo = new ProcessStartInfo(command.FileName)
    {
        UseShellExecute = false,
        CreateNoWindow = true
    };
    foreach (var argument in command.Arguments) startInfo.ArgumentList.Add(argument);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Could not start {command.FileName}.");
    process.WaitForExit();
    if (command.Required && process.ExitCode != 0)
        throw new InvalidOperationException($"{command.FileName} failed with exit code {process.ExitCode}.");
}
static void RegisterTask(ScheduledTaskPlan plan)
{
    var serviceType = Type.GetTypeFromProgID("Schedule.Service")
        ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
    dynamic service = Activator.CreateInstance(serviceType)!;
    service.Connect();
    dynamic folder = service.GetFolder("\\");
    dynamic definition = service.NewTask(0);
    definition.RegistrationInfo.Description = plan.Description;
    definition.Principal.UserId = plan.UserId;
    definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
    definition.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
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
    folder.RegisterTaskDefinition(plan.TaskName, definition, 6, null, null, 3, null); // CREATE_OR_UPDATE, INTERACTIVE_TOKEN
}

static void CreateShortcut(string path, string target, string arguments)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
    dynamic shell = Activator.CreateInstance(shellType)!;
    dynamic shortcut = shell.CreateShortcut(path);
    shortcut.TargetPath = target;
    shortcut.Arguments = arguments;
    shortcut.WorkingDirectory = Path.GetDirectoryName(target);
    shortcut.Save();
}
static void DeleteShortcut(string path) { if (File.Exists(path)) File.Delete(path); }
public sealed record ProcessCommand(string FileName, IReadOnlyList<string> Arguments, bool Required);
public sealed record ScheduledTaskPlan(string TaskName, string EnginePath, string UserId, string Description,
    bool AllowStartOnBattery, bool StopOnBattery, int RestartCount);

public static class SetupTaskPlan
{
    public static ProcessCommand End(string taskName) =>
        new("schtasks.exe", ["/End", "/TN", taskName], false);

    public static ProcessCommand Delete(string taskName) =>
        new("schtasks.exe", ["/Delete", "/TN", taskName, "/F"], false);

    public static ScheduledTaskPlan Define(string taskName, string enginePath, string userId)
    {
        if (!Path.IsPathFullyQualified(enginePath))
            throw new ArgumentException("The engine path must be absolute.", nameof(enginePath));
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("The current user identity is required.", nameof(userId));
        return new ScheduledTaskPlan(taskName, enginePath, userId,
            "Repeatedly locks the workstation during configured restricted hours.",
            AllowStartOnBattery: true, StopOnBattery: false, RestartCount: 3);
    }
}
