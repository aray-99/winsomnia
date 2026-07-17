using System.Text.Json;

namespace Winsomnia.Core;

public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
    DateTimeOffset LocalNow { get; }
}

public sealed class SystemClock : ISystemClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}

public sealed class StateManager(string statePath, ISystemClock clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string StatePath { get; } = Path.GetFullPath(statePath);

    public PersistentState LoadOrCreate(string? legacyConfigPath = null)
    {
        if (File.Exists(StatePath))
        {
            var state = JsonSerializer.Deserialize<PersistentState>(File.ReadAllText(StatePath), JsonOptions)
                ?? throw new InvalidDataException("State file was empty.");
            Validate(state);
            return Normalize(state);
        }

        var migrated = legacyConfigPath is not null && File.Exists(legacyConfigPath)
            ? MigrateLegacy(legacyConfigPath)
            : new PersistentState
            {
                Credit = CreditLedger.Full(CreditPolicy.Standard, clock.UtcNow),
                Armed = false
            };
        Save(migrated);
        return migrated;
    }

    public void Save(PersistentState state)
    {
        Validate(state);
        var parent = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(parent);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, StatePath, true);
    }

    public PersistentState Normalize(PersistentState state)
    {
        var now = clock.UtcNow;
        var settings = state.Settings;
        var pending = state.PendingSettings;
        if (pending is not null && pending.ApplyAtUtc <= now)
        {
            settings = pending.Settings;
            pending = null;
        }

        var policy = settings.EffectiveCredit;
        var checkpoint = state.Credit.LastAccrualUtc;
        // Never move an accrual checkpoint backwards when the system clock rolls back.
        var elapsed = now > checkpoint ? now - checkpoint : TimeSpan.Zero;
        var wholeDays = (int)Math.Floor(elapsed.TotalDays);
        var balance = Math.Min(policy.MaximumMinutes,
            state.Credit.BalanceMinutes + wholeDays * policy.DailyGrantMinutes);
        if (wholeDays > 0) checkpoint = checkpoint.AddDays(wholeDays);

        return state with
        {
            Settings = settings,
            PendingSettings = pending,
            Credit = new CreditLedger(balance, checkpoint),
            OverrideUntilUtc = state.OverrideUntilUtc > now ? state.OverrideUntilUtc : null,
            Sessions = state.Sessions.Where(session => session.EndsAtUtc > now).ToList()
        };
    }

    public PersistentState StageSettings(PersistentState state, UserSettings settings)
    {
        settings.Validate();
        return state with { PendingSettings = new PendingSettings(settings, clock.UtcNow.AddHours(24)) };
    }

    public PersistentState SpendCredit(PersistentState state, int minutes)
    {
        state = Normalize(state);
        if (minutes < 5 || minutes % 5 != 0)
            throw new InvalidDataException("Credit must be spent in 5-minute units.");
        if (minutes > state.Credit.BalanceMinutes)
            throw new InvalidDataException("Insufficient unlock credit.");
        return state with
        {
            Credit = state.Credit with { BalanceMinutes = state.Credit.BalanceMinutes - minutes },
            OverrideUntilUtc = clock.UtcNow.AddMinutes(minutes)
        };
    }

    public PersistentState ScheduleException(PersistentState state, DateOnly date)
    {
        var start = TimeRules.ParseClock(state.Settings.StartTime, nameof(UserSettings.StartTime));
        var localStart = new DateTimeOffset(date.ToDateTime(start), clock.LocalNow.Offset);
        if (localStart.ToUniversalTime() < clock.UtcNow.AddHours(24))
            throw new InvalidDataException("An exception must be scheduled at least 24 hours before restriction starts.");
        var text = date.ToString("yyyy-MM-dd");
        if (state.Exceptions.Any(item => item.LocalDate == text)) return state;
        var exceptions = state.Exceptions.Append(new ScheduledException(text, clock.UtcNow)).ToList();
        return state with { Exceptions = exceptions };
    }

    private PersistentState MigrateLegacy(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidDataException("Only schema version 1 can be migrated.");
        var settings = new UserSettings(
            root.GetProperty("startTime").GetString()!,
            root.GetProperty("endTime").GetString()!,
            true,
            root.GetProperty("intervalSeconds").GetInt32(),
            CreditPolicy.Standard);
        settings.Validate();
        return new PersistentState
        {
            Settings = settings,
            Credit = CreditLedger.Full(settings.EffectiveCredit, clock.UtcNow),
            KillSwitchPath = root.GetProperty("killSwitchPath").GetString()!,
            LogPath = root.GetProperty("logPath").GetString()!,
            Armed = false
        };
    }

    private static void Validate(PersistentState state)
    {
        if (state.SchemaVersion != 2) throw new InvalidDataException("Unsupported state schema version.");
        state.Settings.Validate();
        state.PendingSettings?.Settings.Validate();
        if (!Path.IsPathFullyQualified(state.KillSwitchPath) || !Path.IsPathFullyQualified(state.LogPath))
            throw new InvalidDataException("Safety paths must be absolute.");
        if (state.Credit.BalanceMinutes < 0 || state.Credit.BalanceMinutes > state.Settings.EffectiveCredit.MaximumMinutes)
            throw new InvalidDataException("Credit balance is outside the configured range.");
    }
}
