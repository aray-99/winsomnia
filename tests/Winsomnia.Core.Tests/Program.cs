using Winsomnia.Core;

var tests = new (string Name, Action Run)[]
{
    ("overnight schedule crosses midnight", TestOvernight),
    ("settings wait exactly 24 hours", TestDelayedSettings),
    ("credit is prepaid and capped", TestCredit),
    ("clock rollback grants no credit", TestClockRollback),
    ("exceptions require 24 hours notice", TestExceptionCutoff),
    ("session cancellation is token scoped", TestSessionToken),
    ("focus remains active during bedtime credit", TestArbitration),
    ("schema v1 migration starts disarmed", TestMigration),
    ("schema v2 migration imports settings only", TestV2Migration)
};

var failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed.");
return failed == 0 ? 0 : 1;

static void TestOvernight()
{
    var settings = new UserSettings();
    Assert(TimeRules.IsRestricted(settings, AtLocal(2026, 7, 17, 23, 30)), "23:30 must be restricted.");
    Assert(TimeRules.IsRestricted(settings, AtLocal(2026, 7, 18, 5, 59)), "05:59 must be restricted.");
    Assert(!TimeRules.IsRestricted(settings, AtLocal(2026, 7, 18, 6, 0)), "06:00 must be outside.");
}
static void TestDelayedSettings()
{
    using var fixture = new Fixture();
    var state = fixture.Manager.LoadOrCreate();
    var staged = fixture.Manager.StageSettings(state, state.Settings with { StartTime = "22:00" });
    Assert(staged.PendingSettings?.ApplyAtUtc == fixture.Clock.UtcNow.AddHours(24), "Application time must be 24 hours.");
    fixture.Clock.Advance(TimeSpan.FromHours(23));
    Assert(fixture.Manager.Normalize(staged).Settings.StartTime == "23:00", "Change applied early.");
    fixture.Clock.Advance(TimeSpan.FromHours(1));
    Assert(fixture.Manager.Normalize(staged).Settings.StartTime == "22:00", "Change did not apply.");
}
static void TestCredit()
{
    using var fixture = new Fixture();
    var state = fixture.Manager.LoadOrCreate();
    Assert(state.Credit.BalanceMinutes == 60, "Initial balance must be full.");
    state = fixture.Manager.SpendCredit(state, 15);
    Assert(state.Credit.BalanceMinutes == 45, "Credit must be charged up front.");
    fixture.Clock.Advance(TimeSpan.FromDays(3));
    state = fixture.Manager.Normalize(state);
    Assert(state.Credit.BalanceMinutes == 60, "Credit must cap at 60.");
    AssertThrows(() => fixture.Manager.SpendCredit(state, 7), "Non-five-minute spend must fail.");
}
static void TestClockRollback()
{
    using var fixture = new Fixture();
    var state = fixture.Manager.LoadOrCreate() with { Credit = new CreditLedger(0, fixture.Clock.UtcNow) };
    fixture.Clock.Advance(TimeSpan.FromHours(-12));
    state = fixture.Manager.Normalize(state);
    Assert(state.Credit.BalanceMinutes == 0, "Clock rollback granted credit.");
}
static void TestExceptionCutoff()
{
    using var fixture = new Fixture();
    var state = fixture.Manager.LoadOrCreate();
    var today = DateOnly.FromDateTime(fixture.Clock.LocalNow.Date);
    AssertThrows(() => fixture.Manager.ScheduleException(state, today), "Near exception must fail.");
    var later = DateOnly.FromDateTime(fixture.Clock.LocalNow.Date.AddDays(1));
    state = fixture.Manager.ScheduleException(state, later);
    Assert(state.Exceptions.Count == 1, "Valid exception was not saved.");
}
static void TestSessionToken()
{
    var created = LockSession.Create(SessionKind.Focus, "test-client", DateTimeOffset.UtcNow,
        TimeSpan.FromMinutes(5), 30, 15, true);
    Assert(created.Session.AcceptsToken(created.Token), "Correct token was rejected.");
    Assert(!created.Session.AcceptsToken(new string('0', 64)), "Wrong token was accepted.");
}
static void TestArbitration()
{
    var now = AtLocal(2026, 7, 17, 23, 30);
    var created = LockSession.Create(SessionKind.Focus, "test-client", now.ToUniversalTime(),
        TimeSpan.FromMinutes(5), 30, 15, true);
    var state = new PersistentState
    {
        Armed = true,
        Credit = new CreditLedger(45, now.ToUniversalTime()),
        OverrideUntilUtc = now.ToUniversalTime().AddMinutes(10),
        Sessions = [created.Session]
    };
    var decision = PolicyEvaluator.Evaluate(state, now.ToUniversalTime(), now, true);
    Assert(decision.ShouldLock, "Focus session must remain after bedtime credit.");
    Assert(!decision.BedtimeRestricted, "Credit must suppress only bedtime.");
}
static void TestMigration()
{
    using var fixture = new Fixture();
    var legacy = Path.Combine(fixture.Directory, "config.json");
    File.WriteAllText(legacy, "{\n  \"schemaVersion\": 1,\n  \"startTime\": \"22:30\",\n  \"endTime\": \"05:30\",\n  \"intervalSeconds\": 10,\n  \"killSwitchPath\": \"C:\\\\temp\\\\win-somnia-unlock.txt\",\n  \"logPath\": \"C:\\\\temp\\\\winsomnia.log\"\n}");
    var state = fixture.Manager.LoadOrCreate(legacy);
    Assert(state.Settings.StartTime == "22:30", "Legacy schedule was not imported.");
    Assert(!state.Armed, "Migration must start disarmed.");
}
static void TestV2Migration()
{
    using var fixture = new Fixture();
    var legacy = Path.Combine(fixture.Directory, "state-v2.json");
    File.WriteAllText(legacy, """
    {
      "schemaVersion": 2,
      "settings": { "startTime": "21:15", "endTime": "04:45", "enabled": false, "relockIntervalSeconds": 12,
        "credit": { "dailyGrantMinutes": 5, "maximumMinutes": 30 } },
      "armed": true,
      "activationId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "credit": { "balanceMinutes": 0, "lastAccrualUtc": "2020-01-01T00:00:00Z" },
      "sessions": [{ "id": "00000000-0000-0000-0000-000000000001" }]
    }
    """);
    var state = fixture.Manager.LoadOrCreate(legacy);
    Assert(state.Settings.StartTime == "21:15" && !state.Settings.Enabled, "Version 2 settings were not imported.");
    Assert(!state.Armed && state.ActivationId is null, "Version 2 migration must start disarmed.");
    Assert(state.Sessions.Count == 0 && state.Credit.BalanceMinutes == 30, "Dynamic version 2 state was imported.");
}
static DateTimeOffset AtLocal(int year, int month, int day, int hour, int minute) =>
    new(year, month, day, hour, minute, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(year, month, day, hour, minute, 0)));
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void AssertThrows(Action action, string message) { try { action(); } catch { return; } throw new InvalidOperationException(message); }

sealed class FakeClock : ISystemClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 7, 17, 3, 0, 0, TimeSpan.Zero);
    public DateTimeOffset LocalNow => UtcNow.ToLocalTime();
    public void Advance(TimeSpan value) => UtcNow = UtcNow.Add(value);
}
sealed class Fixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"winsomnia-tests-{Guid.NewGuid():N}");
    public FakeClock Clock { get; } = new();
    public StateManager Manager { get; }
    public Fixture() { System.IO.Directory.CreateDirectory(Directory); Manager = new StateManager(Path.Combine(Directory, "state-v3.json"), Clock); }
    public void Dispose()
    {
        var full = Path.GetFullPath(Directory);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a non-temporary test directory.");
        System.IO.Directory.Delete(full, true);
    }
}
