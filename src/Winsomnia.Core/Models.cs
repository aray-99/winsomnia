using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Winsomnia.Core;

public sealed record CreditPolicy(int DailyGrantMinutes = 10, int MaximumMinutes = 60)
{
    public static CreditPolicy Strict => new(5, 30);
    public static CreditPolicy Standard => new(10, 60);
    public static CreditPolicy Flexible => new(15, 120);

    public void Validate()
    {
        if (DailyGrantMinutes is < 0 or > 1440)
            throw new InvalidDataException("Daily credit grant must be between 0 and 1440 minutes.");
        if (MaximumMinutes is < 5 or > 1440 || MaximumMinutes % 5 != 0)
            throw new InvalidDataException("Maximum credit must be a 5-minute multiple between 5 and 1440.");
        if (DailyGrantMinutes > MaximumMinutes)
            throw new InvalidDataException("Daily credit grant cannot exceed the maximum balance.");
    }
}

public sealed record UserSettings(
    string StartTime = "23:00",
    string EndTime = "06:00",
    bool Enabled = true,
    int RelockIntervalSeconds = 5,
    CreditPolicy? Credit = null)
{
    [JsonIgnore]
    public CreditPolicy EffectiveCredit => Credit ?? CreditPolicy.Standard;

    public void Validate()
    {
        var start = TimeRules.ParseClock(StartTime, nameof(StartTime));
        var end = TimeRules.ParseClock(EndTime, nameof(EndTime));
        if (start == end) throw new InvalidDataException("Start and end times must differ.");
        if (RelockIntervalSeconds is < 1 or > 3600)
            throw new InvalidDataException("Relock interval must be between 1 and 3600 seconds.");
        EffectiveCredit.Validate();
    }
}

public sealed record PendingSettings(UserSettings Settings, DateTimeOffset ApplyAtUtc);

public sealed record ScheduledException(string LocalDate, DateTimeOffset SubmittedAtUtc)
{
    public DateOnly Date => DateOnly.ParseExact(LocalDate, "yyyy-MM-dd");
}

public sealed record CreditLedger(int BalanceMinutes, DateTimeOffset LastAccrualUtc)
{
    public static CreditLedger Full(CreditPolicy policy, DateTimeOffset now) =>
        new(policy.MaximumMinutes, now);
}

public enum SessionKind { Focus, Generic }

public sealed record LockSession(
    Guid Id,
    SessionKind Kind,
    string Source,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    int RelockIntervalSeconds,
    int UnlockGraceSeconds,
    bool Cancelable,
    string CancellationTokenHash,
    DateTimeOffset? GraceUntilUtc = null)
{
    public bool IsActive(DateTimeOffset now) => now >= StartsAtUtc && now < EndsAtUtc;

    public static (LockSession Session, string Token) Create(
        SessionKind kind, string source, DateTimeOffset now, TimeSpan duration,
        int relockIntervalSeconds, int unlockGraceSeconds, bool cancelable)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 100)
            throw new InvalidDataException("Session source is required and limited to 100 characters.");
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromHours(8))
            throw new InvalidDataException("Session duration must be between 1 second and 8 hours.");
        if (relockIntervalSeconds is < 1 or > 3600)
            throw new InvalidDataException("Relock interval must be between 1 and 3600 seconds.");
        if (unlockGraceSeconds is < 0 or > 300)
            throw new InvalidDataException("Unlock grace must be between 0 and 300 seconds.");

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return (new LockSession(Guid.NewGuid(), kind, source, now, now.Add(duration),
            relockIntervalSeconds, unlockGraceSeconds, cancelable, HashToken(token)), token);
    }

    public bool AcceptsToken(string token) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(CancellationTokenHash),
            Convert.FromHexString(HashToken(token)));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}

public sealed record PersistentState
{
    public int SchemaVersion { get; init; } = 3;
    public UserSettings Settings { get; init; } = new();
    public PendingSettings? PendingSettings { get; init; }
    public List<ScheduledException> Exceptions { get; init; } = [];
    public CreditLedger Credit { get; init; } = CreditLedger.Full(CreditPolicy.Standard, DateTimeOffset.UtcNow);
    public DateTimeOffset? OverrideUntilUtc { get; init; }
    public DateTimeOffset? BedtimeGraceUntilUtc { get; init; }
    public List<LockSession> Sessions { get; init; } = [];
    public string LogPath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "winsomnia", "winsomnia.log");
    public bool Armed { get; init; }
    public string? ActivationId { get; init; }
    public DateTimeOffset? LastClaimedWarningTransitionUtc { get; init; }
}

public static class LockAuthorizationStates
{
    public const string Disarmed = "Disarmed";
    public const string Armed = "Armed";
    public const string Faulted = "Faulted";
}

public sealed record LockAuthorization(string State, string Reason);

public sealed record WarningClaim(bool ShouldDisplay, DateTimeOffset? TransitionUtc);

public sealed record EngineStatus(
    UserSettings Settings,
    bool Paused,
    bool Armed,
    LockAuthorization LockAuthorization,
    bool Restricted,
    string Phase,
    DateTimeOffset? NextTransitionUtc,
    int CreditMinutes,
    DateTimeOffset? PendingSettingsApplyAtUtc,
    DateTimeOffset? OverrideUntilUtc,
    DateTimeOffset? GraceUntilUtc,
    IReadOnlyList<LockSession> ActiveSessions,
    string? Error = null);
