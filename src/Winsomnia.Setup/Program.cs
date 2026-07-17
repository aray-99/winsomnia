using System.Diagnostics;

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
        Run("schtasks.exe", $"/End /TN \"{TaskName}\"", false);
        Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", false);
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
    Run("schtasks.exe", $"/End /TN \"{TaskName}\"", false);
    Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F", false);
    CopyDirectory(source, target);
    if (Environment.ProcessPath is not null)
        File.Copy(Environment.ProcessPath, Path.Combine(target, "Winsomnia.Setup.exe"), true);
    var engine = Path.Combine(target, "Winsomnia.Engine.exe");
    var desktop = Path.Combine(target, "Winsomnia.Desktop.exe");
    Run("schtasks.exe", $"/Create /SC ONLOGON /TN \"{TaskName}\" /TR \"\\\"{engine}\\\" --enable-lock\" /RL LIMITED /F", true);
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
static void Run(string fileName, string arguments, bool required)
{
    using var process = Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new InvalidOperationException($"Could not start {fileName}.");
    process.WaitForExit();
    if (required && process.ExitCode != 0) throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}.");
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
