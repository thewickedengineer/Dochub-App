using Dochub.Api.Domain;
using Dochub.Api.Services;

namespace Dochub.Api.Tests;

public class SyncScheduleCalculatorTests
{
    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void Daily_picks_todays_slot_when_it_is_still_ahead()
    {
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(9, 0), "UTC", null, null, Utc("2026-03-10T06:00:00Z"));

        Assert.Equal(Utc("2026-03-10T09:00:00Z"), next);
    }

    [Fact]
    public void Daily_rolls_to_tomorrow_once_todays_slot_has_passed()
    {
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(9, 0), "UTC", null, null, Utc("2026-03-10T09:00:00Z"));

        Assert.Equal(Utc("2026-03-11T09:00:00Z"), next);
    }

    [Fact]
    public void Weekly_lands_on_the_requested_weekday()
    {
        // 2026-03-10 is a Tuesday; the next Monday is the 16th.
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Weekly, new TimeOnly(9, 0), "UTC", dayOfWeek: 1, dayOfMonth: null,
            after: Utc("2026-03-10T12:00:00Z"));

        Assert.Equal(DayOfWeek.Monday, next.UtcDateTime.DayOfWeek);
        Assert.Equal(Utc("2026-03-16T09:00:00Z"), next);
    }

    [Fact]
    public void Monthly_moves_to_next_month_once_the_day_has_passed()
    {
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Monthly, new TimeOnly(9, 0), "UTC", null, dayOfMonth: 5,
            after: Utc("2026-03-10T12:00:00Z"));

        Assert.Equal(Utc("2026-04-05T09:00:00Z"), next);
    }

    [Fact]
    public void Monthly_on_day_28_still_fires_in_february()
    {
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Monthly, new TimeOnly(9, 0), "UTC", null, dayOfMonth: 28,
            after: Utc("2027-02-01T00:00:00Z"));

        Assert.Equal(Utc("2027-02-28T09:00:00Z"), next);
    }

    [Fact]
    public void Local_time_is_held_steady_across_a_dst_change()
    {
        // London moves to BST on 2026-03-29. 09:00 local is 09:00 UTC before the
        // change and 08:00 UTC after it — the wall-clock time people set is what holds.
        var beforeChange = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(9, 0), "Europe/London", null, null, Utc("2026-03-27T12:00:00Z"));
        var afterChange = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(9, 0), "Europe/London", null, null, Utc("2026-03-30T12:00:00Z"));

        Assert.Equal(Utc("2026-03-28T09:00:00Z"), beforeChange);
        Assert.Equal(Utc("2026-03-31T08:00:00Z"), afterChange);
    }

    [Fact]
    public void A_time_that_does_not_exist_on_a_spring_forward_day_is_pushed_past_the_gap()
    {
        // London skips 01:00–02:00 on 2026-03-29, so a 01:30 schedule must not throw.
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(1, 30), "Europe/London", null, null, Utc("2026-03-28T12:00:00Z"));

        Assert.Equal(Utc("2026-03-29T01:30:00Z"), next);
    }

    [Fact]
    public void Every_result_is_expressed_in_utc_so_postgres_accepts_it()
    {
        var next = SyncScheduleCalculator.Next(
            SyncFrequency.Daily, new TimeOnly(9, 0), "Asia/Kolkata", null, null, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.Zero, next.Offset);
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_utc_rather_than_wedging_the_worker()
    {
        Assert.False(SyncScheduleCalculator.IsValidZone("Mars/Olympus_Mons"));
        Assert.Equal(TimeZoneInfo.Utc, SyncScheduleCalculator.ResolveZone("Mars/Olympus_Mons"));
    }
}
