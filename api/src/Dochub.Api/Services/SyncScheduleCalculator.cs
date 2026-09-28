using Dochub.Api.Domain;

namespace Dochub.Api.Services;

/// <summary>
/// Works out when a schedule should next fire. Everything is computed in the
/// schedule's own time zone so "09:00 daily" stays 09:00 for the people who set
/// it, rather than drifting by an hour twice a year.
/// </summary>
public static class SyncScheduleCalculator
{
    public static TimeZoneInfo ResolveZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // An unknown zone must not wedge the worker; UTC keeps it firing.
            return TimeZoneInfo.Utc;
        }
    }

    public static bool IsValidZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>, as a UTC
    /// instant. Postgres <c>timestamptz</c> only accepts offset 0, and the moment
    /// is the same either way — the zone only matters while working out which
    /// wall-clock time was meant.
    /// </summary>
    public static DateTimeOffset Next(
        SyncFrequency frequency, TimeOnly timeOfDay, string timeZoneId,
        int? dayOfWeek, int? dayOfMonth, DateTimeOffset after)
    {
        var zone = ResolveZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(after, zone);
        var candidate = local.Date.Add(timeOfDay.ToTimeSpan());

        switch (frequency)
        {
            case SyncFrequency.Daily:
                if (candidate <= local.DateTime) candidate = candidate.AddDays(1);
                break;

            case SyncFrequency.Weekly:
            {
                var target = Math.Clamp(dayOfWeek ?? (int)local.DayOfWeek, 0, 6);
                var delta = (target - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(delta);
                if (candidate <= local.DateTime) candidate = candidate.AddDays(7);
                break;
            }

            case SyncFrequency.Monthly:
            {
                // Capped at 28 on input, so the day exists in every month and a
                // monthly schedule never silently skips February.
                var day = Math.Clamp(dayOfMonth ?? 1, 1, 28);
                candidate = new DateTime(local.Year, local.Month, day).Add(timeOfDay.ToTimeSpan());
                if (candidate <= local.DateTime)
                    candidate = candidate.AddMonths(1);
                break;
            }
        }

        return ToOffset(candidate, zone).ToUniversalTime();
    }

    /// <summary>
    /// Converts a local wall-clock instant to an absolute one, handling the two
    /// awkward DST cases: a time that does not exist (spring forward) and one
    /// that happens twice (fall back).
    /// </summary>
    private static DateTimeOffset ToOffset(DateTime localWallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        // Skipped hour: push past the gap rather than throwing.
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);

        // Repeated hour: take the first (still-daylight) occurrence.
        var offset = zone.IsAmbiguousTime(unspecified)
            ? zone.GetAmbiguousTimeOffsets(unspecified).Max()
            : zone.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset);
    }
}
