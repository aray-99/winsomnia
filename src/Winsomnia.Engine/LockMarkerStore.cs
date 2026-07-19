using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winsomnia.Core;

namespace Winsomnia.Engine;

public interface ILockMarkerStore
{
    LockAuthorization Inspect(PersistentState state, bool realLockEnabled);
    void Commit(string activationId);
    void Revoke();
}

public sealed class LockMarkerStore : ILockMarkerStore
{
    public const string DefaultPath = @"C:\temp\winsomnia-lock-enabled.json";
    private const int MarkerVersion = 1;
    private const long MaximumMarkerBytes = 4096;

    public LockMarkerStore(string? path = null) => MarkerPath = Path.GetFullPath(path ?? DefaultPath);
    public string MarkerPath { get; }

    public LockAuthorization Inspect(PersistentState state, bool realLockEnabled)
    {
        if (!state.Armed) return new(LockAuthorizationStates.Disarmed, "state-disarmed");
        if (!Guid.TryParseExact(state.ActivationId, "N", out _))
            return new(LockAuthorizationStates.Faulted, "state-activation-id-invalid");
        if (!realLockEnabled) return new(LockAuthorizationStates.Disarmed, "lock-switch-disabled");
        try
        {
            if (!TryGetAttributes(out var attributes))
                return new(LockAuthorizationStates.Disarmed, "marker-missing");
            if ((attributes & FileAttributes.Directory) != 0)
                return new(LockAuthorizationStates.Faulted, "marker-is-directory");
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return new(LockAuthorizationStates.Faulted, "marker-is-reparse-point");
            using var stream = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumMarkerBytes)
                return new(LockAuthorizationStates.Faulted, "marker-too-large");
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var markerVersion) ||
                !root.TryGetProperty("activationId", out var id) || id.ValueKind != JsonValueKind.String)
                return new(LockAuthorizationStates.Faulted, "marker-malformed");
            if (markerVersion != MarkerVersion)
                return new(LockAuthorizationStates.Faulted, "marker-version-unsupported");
            var markerId = id.GetString();
            if (!Guid.TryParseExact(markerId, "N", out _))
                return new(LockAuthorizationStates.Faulted, "marker-activation-id-invalid");
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(markerId), Encoding.ASCII.GetBytes(state.ActivationId)))
                return new(LockAuthorizationStates.Faulted, "marker-activation-id-mismatch");
            return new(LockAuthorizationStates.Armed, "marker-validated");
        }
        catch (JsonException)
        {
            return new(LockAuthorizationStates.Faulted, "marker-malformed");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(LockAuthorizationStates.Faulted, "marker-io-failure");
        }
    }

    public void Commit(string activationId)
    {
        if (!Guid.TryParseExact(activationId, "N", out _))
            throw new InvalidDataException("A valid activation ID is required.");
        var parent = Path.GetDirectoryName(MarkerPath)
            ?? throw new InvalidDataException("Marker path has no parent directory.");
        Directory.CreateDirectory(parent);
        if (TryGetAttributes(out var existingAttributes))
        {
            if ((existingAttributes & FileAttributes.Directory) != 0)
                throw new InvalidOperationException("The marker path is a directory.");
            if ((existingAttributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The marker path is a reparse point.");
        }
        var temporary = Path.Combine(parent, $".winsomnia-marker-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { version = MarkerVersion, activationId }));
            File.Move(temporary, MarkerPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Revoke()
    {
        if (!TryGetAttributes(out var attributes)) return;
        if ((attributes & FileAttributes.Directory) != 0)
            throw new InvalidOperationException("The marker path is a directory.");
        File.Delete(MarkerPath);
    }

    private bool TryGetAttributes(out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(MarkerPath);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }
}
