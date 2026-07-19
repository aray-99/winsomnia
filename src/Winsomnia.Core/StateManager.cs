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

    public PersistentState LoadOrCreate(string? legacyStatePath = null, string? legacyConfigPath = null)
    {
        if (File.Exists(StatePath)) return LoadCurrentOrMigrate(StatePath);
        var migrationPath = new[] { legacyStatePath, legacyConfigPath }
            .FirstOrDefault(path => path is not null && File.Exists(path));
        var state = migrationPath is null ? CreateDisarmed() : MigrateSettingsOnly(migrationPath);
        Save(state);
        return state;
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

    private PersistentState LoadCurrentOrMigrate(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var schema = ReadSchema(root);
        if (schema is 1 or 2)
        {
            var migrated = MigrateSettingsOnly(root, schema);
            Save(migrated);
            return migrated;
        }
        if (schema != 3) throw new InvalidDataException("Unsupported state schema version.");
        var state = root.Deserialize<PersistentState>(JsonOptions)
            ?? throw new InvalidDataException("State file was empty.");
        Validate(state);
        return Normalize(state);
    }

    private PersistentState MigrateSettingsOnly(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return MigrateSettingsOnly(document.RootElement, ReadSchema(document.RootElement));
    }

    private PersistentState MigrateSettingsOnly(JsonElement root, int schema)
    {
        UserSettings settings = schema switch
        {
            1 => new UserSettings(GetProperty(root, "startTime").GetString()!,
                GetProperty(root, "endTime").GetString()!, true,
                GetProperty(root, "intervalSeconds").GetInt32(), CreditPolicy.Standard),
            2 => GetProperty(root, "settings").Deserialize<UserSettings>(JsonOptions)
                ?? throw new InvalidDataException("Version 2 settings were empty."),
            _ => throw new InvalidDataException("Only schema versions 1 and 2 can be migrated.")
        };
        settings.Validate();
        return new PersistentState
        {
            Settings = settings,
            Credit = CreditLedger.Full(settings.EffectiveCredit, clock.UtcNow),
            Armed = false,
            ActivationId = null
        };
    }

    private PersistentState CreateDisarmed() => new()
    {
        Credit = CreditLedger.Full(CreditPolicy.Standard, clock.UtcNow),
        Armed = false,
        ActivationId = null
    };

    private static int ReadSchema(JsonElement root)
    {
        if (!TryGetProperty(root, "schemaVersion", out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var version))
            throw new InvalidDataException("State schema version is missing or invalid.");
        return version;
    }

    private static JsonElement GetProperty(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value)
            ? value
            : throw new InvalidDataException($"Required property '{name}' is missing.");

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static void Validate(PersistentState state)
    {
        if (state.SchemaVersion != 3) throw new InvalidDataException("Unsupported state schema version.");
        state.Settings.Validate();
        state.PendingSettings?.Settings.Validate();
        if (!Path.IsPathFullyQualified(state.LogPath)) throw new InvalidDataException("Log path must be absolute.");
        if (state.Armed && !Guid.TryParseExact(state.ActivationId, "N", out _))
            throw new InvalidDataException("Armed state requires a valid activation ID.");
        if (!state.Armed && state.ActivationId is not null)
            throw new InvalidDataException("Disarmed state cannot retain an activation ID.");
        if (state.Credit.BalanceMinutes < 0 || state.Credit.BalanceMinutes > state.Settings.EffectiveCredit.MaximumMinutes)
            throw new InvalidDataException("Credit balance is outside the configured range.");
    }
}
