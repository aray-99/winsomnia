using System.Globalization;

namespace Winsomnia.Core;

public static class TimeRules
{
    public static TimeOnly ParseClock(string value, string parameterName)
    {
        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            throw new InvalidDataException($"{parameterName} must use 24-hour HH:mm format.");
        return parsed;
    }

    public static bool IsRestricted(UserSettings settings, DateTimeOffset localNow)
    {
        settings.Validate();
        if (!settings.Enabled) return false;
        var start = ParseClock(settings.StartTime, nameof(settings.StartTime));
        var end = ParseClock(settings.EndTime, nameof(settings.EndTime));
        var now = TimeOnly.FromDateTime(localNow.DateTime);
        return start < end ? now >= start && now < end : now >= start || now < end;
    }

    public static DateOnly RestrictionStartDate(UserSettings settings, DateTimeOffset localNow)
    {
        var start = ParseClock(settings.StartTime, nameof(settings.StartTime));
        var end = ParseClock(settings.EndTime, nameof(settings.EndTime));
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var now = TimeOnly.FromDateTime(localNow.DateTime);
        if (start > end && now < end) return today.AddDays(-1);
        return today;
    }

    public static DateTimeOffset NextTransition(UserSettings settings, DateTimeOffset localNow)
    {
        var start = ParseClock(settings.StartTime, nameof(settings.StartTime));
        var end = ParseClock(settings.EndTime, nameof(settings.EndTime));
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var target = IsRestricted(settings, localNow) ? end : start;
        var date = today;
        var candidate = new DateTimeOffset(date.ToDateTime(target), localNow.Offset);
        if (candidate <= localNow) candidate = candidate.AddDays(1);
        return candidate;
    }
}
