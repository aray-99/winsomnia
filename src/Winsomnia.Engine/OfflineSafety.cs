namespace Winsomnia.Engine;

public static class OfflineSafety
{
    public static void Pause(Winsomnia.Core.StateManager manager, string? legacyConfigPath = null)
    {
        var state = manager.LoadOrCreate(legacyConfigPath);
        var parent = Path.GetDirectoryName(state.KillSwitchPath)
            ?? throw new InvalidDataException("Kill-switch path has no parent directory.");
        Directory.CreateDirectory(parent);
        if (!File.Exists(state.KillSwitchPath) && !Directory.Exists(state.KillSwitchPath))
            File.WriteAllText(state.KillSwitchPath, string.Empty);
        manager.Save(state with { Armed = false });
    }
}
