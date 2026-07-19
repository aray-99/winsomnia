using System.Text.Json;

namespace Winsomnia.Setup;

public interface IUpgradeFileSystem
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path);
    void MoveDirectory(string source, string destination);
    string ReadAllText(string path);
    void WriteAllTextAtomic(string path, string content);
    void DeleteFile(string path);
}

public sealed class UpgradeFileSystem : IUpgradeFileSystem
{
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void MoveDirectory(string source, string destination) => Directory.Move(source, destination);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        Directory.Delete(path, recursive: (attributes & FileAttributes.ReparsePoint) == 0);
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public sealed record UpgradeLocations(string Target, string Stage, string Backup, string Journal)
{
    public static UpgradeLocations ForTarget(string targetPath)
    {
        var target = Path.GetFullPath(targetPath);
        var parent = Path.GetDirectoryName(target)
            ?? throw new InvalidDataException("The installation target has no parent.");
        return new(target,
            Path.Combine(parent, ".winsomnia-upgrade-stage"),
            Path.Combine(parent, ".winsomnia-upgrade-backup"),
            Path.Combine(parent, ".winsomnia-upgrade.json"));
    }
}

public static class UpgradePhases
{
    public const string Prepared = "Prepared";
    public const string OldMoved = "OldMoved";
    public const string NewMoved = "NewMoved";
    public const string Committed = "Committed";
}

public sealed record UpgradeJournal(int Version, string Target, string Phase);

public sealed class DurableInstallationRecovery(IUpgradeFileSystem files)
{
    public void Recover(string targetPath)
    {
        var locations = UpgradeLocations.ForTarget(targetPath);
        if (!files.FileExists(locations.Journal))
        {
            RecoverWithoutJournal(locations);
            return;
        }

        var journal = JsonSerializer.Deserialize<UpgradeJournal>(files.ReadAllText(locations.Journal))
            ?? throw new InvalidDataException("Upgrade journal is empty.");
        if (journal.Version != 1 || !Path.GetFullPath(journal.Target).Equals(locations.Target,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Upgrade journal does not match the installation target.");

        switch (journal.Phase)
        {
            case UpgradePhases.Prepared:
                RestoreOldIfMoved(locations);
                CleanupStage(locations);
                break;
            case UpgradePhases.OldMoved:
            case UpgradePhases.NewMoved:
                RollBackToOld(locations);
                break;
            case UpgradePhases.Committed:
                if (!files.DirectoryExists(locations.Target))
                    throw new InvalidDataException("Committed installation target is missing.");
                CleanupDirectory(locations.Backup);
                CleanupStage(locations);
                break;
            default:
                throw new InvalidDataException("Upgrade journal phase is unsupported.");
        }
        files.DeleteFile(locations.Journal);
    }

    public void Save(UpgradeLocations locations, string phase) =>
        files.WriteAllTextAtomic(locations.Journal,
            JsonSerializer.Serialize(new UpgradeJournal(1, locations.Target, phase)));

    private void RecoverWithoutJournal(UpgradeLocations locations)
    {
        if (files.DirectoryExists(locations.Backup))
        {
            if (!files.DirectoryExists(locations.Target))
                files.MoveDirectory(locations.Backup, locations.Target);
            else
                CleanupDirectory(locations.Backup);
        }
        CleanupStage(locations);
    }

    private void RestoreOldIfMoved(UpgradeLocations locations)
    {
        if (!files.DirectoryExists(locations.Backup)) return;
        if (files.DirectoryExists(locations.Target))
            throw new InvalidDataException("Prepared recovery found both target and backup.");
        files.MoveDirectory(locations.Backup, locations.Target);
    }

    private void RollBackToOld(UpgradeLocations locations)
    {
        CleanupDirectory(locations.Target);
        if (files.DirectoryExists(locations.Backup))
            files.MoveDirectory(locations.Backup, locations.Target);
        CleanupStage(locations);
    }

    private void CleanupStage(UpgradeLocations locations) => CleanupDirectory(locations.Stage);

    private void CleanupDirectory(string path)
    {
        if (files.DirectoryExists(path)) files.DeleteDirectory(path);
    }
}

public sealed class DurableInstallationSwap : IInstallationSwap
{
    private readonly IUpgradeFileSystem files;
    private readonly DurableInstallationRecovery recovery;
    private readonly UpgradeLocations locations;
    private bool disposed;

    public DurableInstallationSwap(IUpgradeFileSystem files, string stagedPath, string targetPath)
    {
        this.files = files;
        recovery = new(files);
        locations = UpgradeLocations.ForTarget(targetPath);
        if (!Path.GetFullPath(stagedPath).Equals(locations.Stage, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staged payload is not at the deterministic upgrade path.");
        if (!files.DirectoryExists(locations.Stage))
            throw new DirectoryNotFoundException("The staged installation is missing.");
        if (files.DirectoryExists(locations.Backup) || files.FileExists(locations.Journal))
            throw new InvalidOperationException("Previous upgrade recovery has not completed.");

        try
        {
            recovery.Save(locations, UpgradePhases.Prepared);
            if (files.DirectoryExists(locations.Target)) files.MoveDirectory(locations.Target, locations.Backup);
            recovery.Save(locations, UpgradePhases.OldMoved);
            files.MoveDirectory(locations.Stage, locations.Target);
            recovery.Save(locations, UpgradePhases.NewMoved);
        }
        catch (Exception primary)
        {
            try { recovery.Recover(locations.Target); }
            catch (Exception rollback)
            {
                throw new AggregateException("Installation replacement and rollback both failed.", primary, rollback);
            }
            throw;
        }
    }

    public void Commit()
    {
        if (disposed) throw new ObjectDisposedException(nameof(DurableInstallationSwap));
        recovery.Save(locations, UpgradePhases.Committed);
        recovery.Recover(locations.Target);
    }

    public void Dispose()
    {
        if (disposed) return;
        try
        {
            recovery.Recover(locations.Target);
            disposed = true;
        }
        catch
        {
            // Leave the journal for the next startup barrier to retry.
            throw;
        }
    }
}
