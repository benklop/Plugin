using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Moonfin.Households;

/// <summary>
/// One access schedule entry: a day, or a group of days, and the hours in it when the account may
/// be used. Day numbers follow Jellyfin's DynamicDayOfWeek: 0 to 6 are Sunday to Saturday, then
/// 7 every day, 8 weekdays and 9 weekends. Hours are on the server's clock.
/// </summary>
public readonly struct ScheduleEntry : IEquatable<ScheduleEntry>
{
    public ScheduleEntry(int dayOfWeek, double startHour, double endHour)
    {
        DayOfWeek = dayOfWeek;
        StartHour = startHour;
        EndHour = endHour;
    }

    public int DayOfWeek { get; }

    public double StartHour { get; }

    public double EndHour { get; }

    public bool Equals(ScheduleEntry other) =>
        DayOfWeek == other.DayOfWeek && StartHour.Equals(other.StartHour) && EndHour.Equals(other.EndHour);

    public override bool Equals(object? obj) => obj is ScheduleEntry other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(DayOfWeek, StartHour, EndHour);
}

/// <summary>
/// Access schedules as Jellyfin judges them (UserEntityExtensions.IsParentalScheduleAllowed): an
/// account with no entries is always allowed, otherwise it is allowed while the server's local
/// time falls on one entry's days, from its start hour up to and including its end hour.
/// </summary>
public static class AccessScheduleRules
{
    public const int MaxEntries = 50;

    public const int Everyday = 7;
    public const int Weekday = 8;
    public const int Weekend = 9;

    /// <summary>Jellyfin's names for each day number, as its JSON writes them.</summary>
    public static readonly IReadOnlyList<string> DayNames =
    [
        "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday",
        "Everyday", "Weekday", "Weekend",
    ];

    public static string DayName(int day) =>
        day >= 0 && day < DayNames.Count ? DayNames[day] : day.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a day as Jellyfin's name for it or as its number, or null when it is neither.</summary>
    public static int? ParseDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number >= 0 && number < DayNames.Count ? number : null;
        }

        for (var i = 0; i < DayNames.Count; i++)
        {
            if (string.Equals(DayNames[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>Whether an entry's days include <paramref name="day"/>.</summary>
    public static bool Covers(int entryDay, DayOfWeek day) => entryDay switch
    {
        Everyday => true,
        Weekday => day is not System.DayOfWeek.Saturday and not System.DayOfWeek.Sunday,
        Weekend => day is System.DayOfWeek.Saturday or System.DayOfWeek.Sunday,
        _ => entryDay == (int)day,
    };

    /// <summary>Null when the entries can be saved, else why not.</summary>
    public static string? Validate(IReadOnlyList<ScheduleEntry>? entries)
    {
        if (entries == null)
        {
            return "A schedule is required; send an empty list for no schedule";
        }

        if (entries.Count > MaxEntries)
        {
            return $"A schedule has at most {MaxEntries} entries";
        }

        foreach (var entry in entries)
        {
            if (entry.DayOfWeek < 0 || entry.DayOfWeek >= DayNames.Count)
            {
                return "Each entry needs a day: a weekday name, Everyday, Weekday or Weekend";
            }

            if (!IsHour(entry.StartHour) || !IsHour(entry.EndHour))
            {
                return "Hours run from 0 to 24";
            }

            if (entry.StartHour >= entry.EndHour)
            {
                return "Each entry has to end after it starts";
            }
        }

        return null;
    }

    private static bool IsHour(double hour) => !double.IsNaN(hour) && hour >= 0 && hour <= 24;

    /// <summary>Whether the account may be used at <paramref name="local"/> on the server's clock.</summary>
    public static bool IsAllowed(IReadOnlyList<ScheduleEntry>? entries, DateTime local)
    {
        if (entries == null || entries.Count == 0)
        {
            return true;
        }

        var hour = local.TimeOfDay.TotalHours;
        return entries.Any(e => Covers(e.DayOfWeek, local.DayOfWeek) && hour >= e.StartHour && hour <= e.EndHour);
    }

    /// <summary>
    /// The next time on the server's clock, after <paramref name="local"/>, when the account may
    /// be used, or null when it may be used now or no entry ever allows it.
    /// </summary>
    public static DateTime? NextAllowed(IReadOnlyList<ScheduleEntry>? entries, DateTime local)
    {
        if (entries == null || IsAllowed(entries, local))
        {
            return null;
        }

        DateTime? best = null;
        var today = local.Date;
        // A week and a day reaches the same weekday again, so every entry is seen at least once
        for (var offset = 0; offset <= 7; offset++)
        {
            var date = today.AddDays(offset);
            foreach (var entry in entries)
            {
                if (!Covers(entry.DayOfWeek, date.DayOfWeek))
                {
                    continue;
                }

                var start = date.AddHours(entry.StartHour);
                if (start > local && (best == null || start < best))
                {
                    best = start;
                }
            }

            if (best != null)
            {
                return best;
            }
        }

        return best;
    }

    /// <summary>
    /// Turns a time on the server's clock into an instant. A time that the clock skips when it
    /// springs forward is moved to just after the jump, which is when the account opens.
    /// </summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var guard = 0;
        while (zone.IsInvalidTime(unspecified) && guard++ < 24 * 60)
        {
            unspecified = unspecified.AddMinutes(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
