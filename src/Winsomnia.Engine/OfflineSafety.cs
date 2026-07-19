using Winsomnia.Core;

namespace Winsomnia.Engine;

public static class OfflineSafety
{
    public static void Pause(StateManager manager, ILockMarkerStore markerStore,
        string? legacyStatePath = null, string? legacyConfigPath = null)
    {
        var state = manager.LoadOrCreate(legacyStatePath, legacyConfigPath);
        manager.Save(state with { Armed = false, ActivationId = null });
        markerStore.Revoke();
    }

    public static void Activate(StateManager manager, ILockMarkerStore markerStore,
        string? legacyStatePath = null, string? legacyConfigPath = null)
    {
        var state = manager.LoadOrCreate(legacyStatePath, legacyConfigPath);
        var activationId = Guid.NewGuid().ToString("N");
        try
        {
            manager.Save(state with { Armed = true, ActivationId = activationId });
            markerStore.Commit(activationId);
        }
        catch
        {
            try { markerStore.Revoke(); } catch { }
            try { manager.Save(state with { Armed = false, ActivationId = null }); } catch { }
            throw;
        }
    }
}
