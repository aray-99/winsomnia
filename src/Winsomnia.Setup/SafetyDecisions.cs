using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Winsomnia.Setup;

public static class TaskStateDecision
{
    public const int Queued = 2;
    public const int Running = 4;
    public static bool MustStop(int state) => state is Queued or Running;
    public static bool IsSafelyDisabled(bool enabled, int state) => !enabled && !MustStop(state);
}

public static class RuntimeProcessDecision
{
    public static bool IsInstalledRuntime(string processName, string? executablePath, string? commandLine,
        string installedRoot)
    {
        var root = Path.GetFullPath(installedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (processName.Equals("Winsomnia.Engine", StringComparison.OrdinalIgnoreCase) &&
            executablePath is not null && IsUnderRoot(executablePath, root) &&
            Path.GetFileName(executablePath).Equals("Winsomnia.Engine.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (processName is not ("powershell" or "powershell.exe" or "pwsh" or "pwsh.exe")) return false;
        if (string.IsNullOrWhiteSpace(commandLine)) return false;
        var monitor = Path.Combine(root, "winsomnia-monitor.ps1");
        return commandLine.Contains(monitor, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string? path, string root) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
}

public static class SetupPathPolicy
{
    public static bool IsInsideTarget(string setupExecutable, string targetPath)
    {
        var target = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(setupExecutable).StartsWith(target, StringComparison.OrdinalIgnoreCase);
    }
}

public static partial class PayloadValidator
{
    private static readonly IReadOnlyDictionary<string, string> RequiredExecutables =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Winsomnia.Engine.exe"] = "Winsomnia.Engine",
            ["Winsomnia.Desktop.exe"] = "Winsomnia.Desktop",
            ["Winsomnia.Setup.exe"] = "Winsomnia.Setup"
        };

    public static void Validate(string root)
    {
        foreach (var required in RequiredExecutables)
            ValidateExecutable(Path.Combine(root, required.Key), required.Value);
        var versionPath = Path.Combine(root, "VERSION");
        if (!File.Exists(versionPath)) throw new InvalidDataException("Installer payload is missing VERSION.");
        var version = File.ReadAllText(versionPath).Trim();
        if (!SemanticVersion().IsMatch(version))
            throw new InvalidDataException("Installer VERSION is not a supported semantic version.");
    }

    public static void ValidateExecutable(string path, string expectedProduct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 4096)
            throw new InvalidDataException($"Installer executable is missing or too small: {Path.GetFileName(path)}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.CoffHeader.Machine == 0)
            throw new InvalidDataException($"Installer executable is not a valid PE image: {Path.GetFileName(path)}");
        var version = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(version.ProductName, expectedProduct, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(version.FileVersion))
            throw new InvalidDataException($"Installer executable identity is invalid: {Path.GetFileName(path)}");
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();
}

