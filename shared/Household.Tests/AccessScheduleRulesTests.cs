using System;
using System.Collections.Generic;
using Xunit;

namespace Moonfin.Households.Tests;

public class AccessScheduleRulesTests
{
    // 2026-11-02 is a Monday
    private static readonly DateTime Monday = new(2026, 11, 2);

    [Fact]
    public void No_entries_means_always_allowed()
    {
        Assert.True(AccessScheduleRules.IsAllowed([], Monday.AddHours(3)));
        Assert.True(AccessScheduleRules.IsAllowed(null, Monday.AddHours(3)));
        Assert.Null(AccessScheduleRules.NextAllowed([], Monday));
    }

    [Theory]
    [InlineData(16.0, false)]
    [InlineData(17.0, true)]
    [InlineData(19.5, true)]
    [InlineData(20.0, true)] // Jellyfin's end hour is inclusive
    [InlineData(20.01, false)]
    public void Hours_are_judged_like_jellyfin(double hour, bool allowed)
    {
        var entries = new List<ScheduleEntry> { new(AccessScheduleRules.Weekday, 17, 20) };

        Assert.Equal(allowed, AccessScheduleRules.IsAllowed(entries, Monday.AddHours(hour)));
    }

    [Fact]
    public void Day_groups_cover_the_right_days()
    {
        Assert.True(AccessScheduleRules.Covers(AccessScheduleRules.Weekday, DayOfWeek.Friday));
        Assert.False(AccessScheduleRules.Covers(AccessScheduleRules.Weekday, DayOfWeek.Saturday));
        Assert.True(AccessScheduleRules.Covers(AccessScheduleRules.Weekend, DayOfWeek.Sunday));
        Assert.False(AccessScheduleRules.Covers(AccessScheduleRules.Weekend, DayOfWeek.Monday));
        Assert.True(AccessScheduleRules.Covers(AccessScheduleRules.Everyday, DayOfWeek.Wednesday));
        Assert.True(AccessScheduleRules.Covers((int)DayOfWeek.Tuesday, DayOfWeek.Tuesday));
        Assert.False(AccessScheduleRules.Covers((int)DayOfWeek.Tuesday, DayOfWeek.Monday));
    }

    [Fact]
    public void Next_allowed_is_later_today_when_an_entry_starts_later()
    {
        var entries = new List<ScheduleEntry> { new(AccessScheduleRules.Everyday, 17, 20) };

        Assert.Equal(Monday.AddHours(17), AccessScheduleRules.NextAllowed(entries, Monday.AddHours(9)));
    }

    [Fact]
    public void Next_allowed_skips_to_the_weekend()
    {
        var entries = new List<ScheduleEntry> { new(AccessScheduleRules.Weekend, 8.5, 21) };

        // Saturday 2026-11-07 at 08:30
        Assert.Equal(new DateTime(2026, 11, 7, 8, 30, 0), AccessScheduleRules.NextAllowed(entries, Monday.AddHours(22)));
    }

    [Fact]
    public void Next_allowed_wraps_to_the_same_weekday_next_week()
    {
        var entries = new List<ScheduleEntry> { new((int)DayOfWeek.Monday, 7, 8) };

        Assert.Equal(Monday.AddDays(7).AddHours(7), AccessScheduleRules.NextAllowed(entries, Monday.AddHours(9)));
    }

    [Fact]
    public void Next_allowed_is_null_while_allowed()
    {
        var entries = new List<ScheduleEntry> { new(AccessScheduleRules.Everyday, 0, 24) };

        Assert.Null(AccessScheduleRules.NextAllowed(entries, Monday.AddHours(12)));
    }

    [Theory]
    [InlineData(0, 0.0, 24.0, true)]
    [InlineData(9, 6.5, 22.25, true)]
    [InlineData(10, 1.0, 2.0, false)] // no such day
    [InlineData(-1, 1.0, 2.0, false)]
    [InlineData(7, 5.0, 5.0, false)] // ends as it starts
    [InlineData(7, 6.0, 5.0, false)]
    [InlineData(7, -1.0, 5.0, false)]
    [InlineData(7, 1.0, 25.0, false)]
    [InlineData(7, double.NaN, 5.0, false)]
    public void Entries_are_validated(int day, double start, double end, bool valid)
    {
        var error = AccessScheduleRules.Validate([new ScheduleEntry(day, start, end)]);

        Assert.Equal(valid, error == null);
    }

    [Fact]
    public void Too_many_entries_are_refused()
    {
        var entries = new List<ScheduleEntry>();
        for (var i = 0; i <= AccessScheduleRules.MaxEntries; i++)
        {
            entries.Add(new ScheduleEntry(AccessScheduleRules.Everyday, 1, 2));
        }

        Assert.NotNull(AccessScheduleRules.Validate(entries));
        Assert.NotNull(AccessScheduleRules.Validate(null));
        Assert.Null(AccessScheduleRules.Validate([]));
    }

    [Theory]
    [InlineData("Weekday", 8)]
    [InlineData("weekend", 9)]
    [InlineData(" Sunday ", 0)]
    [InlineData("3", 3)]
    [InlineData("10", null)]
    [InlineData("Someday", null)]
    [InlineData("", null)]
    public void Days_are_read_by_name_or_number(string value, int? expected)
    {
        Assert.Equal(expected, AccessScheduleRules.ParseDay(value));
    }

    [Fact]
    public void A_time_the_clock_skips_opens_just_after_the_jump()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
        catch (TimeZoneNotFoundException)
        {
            return;
        }

        // 2026-03-08 02:30 doesn't exist in New York; clocks go from 02:00 to 03:00 EDT (07:00Z)
        var instant = AccessScheduleRules.ToInstant(new DateTime(2026, 3, 8, 2, 30, 0), zone);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero), instant);
    }
}
