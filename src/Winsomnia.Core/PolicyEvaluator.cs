namespace Winsomnia.Core;

public sealed record PolicyDecision(
    bool ShouldLock,
    bool BedtimeRestricted,
    int RelockIntervalSeconds,
    IReadOnlyList<LockSession> ActiveSessions,
    string Phase,
    DateTimeOffset? NextTransitionUtc);

public static class PolicyEvaluator
{
    public static PolicyDecision Evaluate(PersistentState rawState, DateTimeOffset utcNow, DateTimeOffset localNow,
        bool lockingAuthorized)
    {
        var sessions = rawState.Sessions.Where(session => session.IsActive(utcNow)).ToList();
        if (!lockingAuthorized)
            return new(false, false, rawState.Settings.RelockIntervalSeconds, sessions, "paused", null);

        var exception = rawState.Exceptions.Any(item => item.Date == TimeRules.RestrictionStartDate(rawState.Settings, localNow));
        var scheduleActive = TimeRules.IsRestricted(rawState.Settings, localNow) && !exception;
        var overridden = rawState.OverrideUntilUtc is not null && rawState.OverrideUntilUtc > utcNow;
        var bedtimeGrace = rawState.BedtimeGraceUntilUtc is not null && rawState.BedtimeGraceUntilUtc > utcNow;
        var bedtime = scheduleActive && !overridden && !bedtimeGrace;
        var activeSessions = sessions.Where(session => session.GraceUntilUtc is null || session.GraceUntilUtc <= utcNow).ToList();
        var shouldLock = bedtime || activeSessions.Count > 0;
        var interval = new[] { bedtime ? rawState.Settings.RelockIntervalSeconds : int.MaxValue }
            .Concat(activeSessions.Select(session => session.RelockIntervalSeconds)).Min();
        if (interval == int.MaxValue) interval = rawState.Settings.RelockIntervalSeconds;
        var next = TimeRules.NextTransition(rawState.Settings, localNow).ToUniversalTime();
        var warning = rawState.Settings.Enabled && !exception && !scheduleActive && !overridden &&
            !bedtimeGrace && next > utcNow && next - utcNow <= TimeSpan.FromMinutes(5);
        var phase = overridden ? "credit-override" : bedtimeGrace ? "restriction-prompt" :
            shouldLock ? "restricted" : warning ? "warning" : "waiting";
        return new(shouldLock, bedtime, interval, sessions, phase, next);
    }
}
