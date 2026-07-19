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

public static partial class RuntimeProcessDecision
{
    public static bool IsInstalledRuntime(string processName, string? executablePath, string? commandLine,
        string installedRoot)
    {
        var root = Path.GetFullPath(installedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (processName.Equals("Winsomnia.Engine", StringComparison.OrdinalIgnoreCase) &&
            executablePath is not null && IsUnderRoot(executablePath, root) &&
            Path.GetFileName(executablePath).Equals("Winsomnia.Engine.exe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!IsPowerShell(processName) || string.IsNullOrWhiteSpace(commandLine)) return false;
        var match = PowerShellFileArgument().Match(commandLine);
        if (!match.Success) return false;
        var script = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return Path.GetFileName(script).Equals("winsomnia-monitor.ps1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShell(string processName) =>
        processName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
        processName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderRoot(string path, string root) =>
        Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?:^|\s)-(?:File|f)\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PowerShellFileArgument();
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
        var versionPath = Path.Combine(root, "VERSION");
        if (!File.Exists(versionPath)) throw new InvalidDataException("Installer payload is missing VERSION.");
        var expectedVersion = NormalizeSemanticVersion(File.ReadAllText(versionPath).Trim());
        foreach (var required in RequiredExecutables)
            ValidateExecutable(Path.Combine(root, required.Key), required.Value, expectedVersion);
    }

    public static void ValidateExecutable(string path, string expectedProduct, Version expectedVersion)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 4096)
            throw new InvalidDataException($"Installer executable is missing or too small: {Path.GetFileName(path)}");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (reader.PEHeaders.PEHeader is null || reader.PEHeaders.CoffHeader.Machine == 0)
            throw new InvalidDataException($"Installer executable is not a valid PE image: {Path.GetFileName(path)}");
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.ProductName, expectedProduct, StringComparison.Ordinal) ||
            NormalizeExecutableVersion(info.FileVersion) != expectedVersion ||
            NormalizeExecutableVersion(info.ProductVersion) != expectedVersion)
            throw new InvalidDataException($"Installer executable identity or version is invalid: {Path.GetFileName(path)}");
    }

    public static Version NormalizeSemanticVersion(string value)
    {
        var match = SemanticVersion().Match(value);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed))
            throw new InvalidDataException("Installer VERSION is not a supported semantic version.");
        return new Version(parsed.Major, parsed.Minor, parsed.Build, 0);
    }

    private static Version NormalizeExecutableVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Executable version is missing.");
        var numeric = value.Split('+', '-', ' ')[0];
        if (!Version.TryParse(numeric, out var parsed) || parsed.Build < 0)
            throw new InvalidDataException("Executable version is invalid.");
        return new Version(parsed.Major, parsed.Minor, parsed.Build, parsed.Revision < 0 ? 0 : parsed.Revision);
    }

    [GeneratedRegex(@"^(\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();
}
