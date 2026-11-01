using System;
using System.Collections.Generic;
using System.Linq;

namespace Moonfin.Households;

/// <summary>
/// A group of accounts an administrator has put together, such as one family. Its managers can
/// look after everyone in it, other managers included: sign a device in for them, reset their PIN
/// and change their access schedule. Administrators never belong to one.
/// </summary>
public class Household
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>User ids, "N" format, of the people who look after this household.</summary>
    public List<string> ManagerUserIds { get; set; } = new();

    /// <summary>User ids, "N" format, of the other accounts in it.</summary>
    public List<string> MemberUserIds { get; set; } = new();
}

/// <summary>What the server knows about a user when households are saved.</summary>
public enum HouseholdUserKind
{
    /// <summary>Not a user on this server.</summary>
    Unknown,
    User,
    Administrator,
}

/// <summary>The households as saved, and the ids that were left out and why.</summary>
public sealed class SanitizedHouseholds
{
    public SanitizedHouseholds(List<Household> households, List<string> dropped)
    {
        Households = households;
        Dropped = dropped;
    }

    public List<Household> Households { get; }

    /// <summary>Plain-language notes about each id that was left out.</summary>
    public List<string> Dropped { get; }
}

/// <summary>
/// The rules for who may manage whom. Kept free of Jellyfin so they can be tested on their own.
/// </summary>
public static class HouseholdRules
{
    public const int MaxHouseholds = 50;
    public const int MaxUsersPerHousehold = 50;
    public const int MaxNameLength = 80;

    /// <summary>Reads an id the way the config stores it, or null when it isn't one.</summary>
    public static Guid? ParseId(string? id) =>
        Guid.TryParse(id, out var parsed) && parsed != Guid.Empty ? parsed : null;

    /// <summary>
    /// Cleans households before they are saved. Ids are put in "N" format and de-duplicated, ids
    /// that aren't users or are administrators are left out, someone listed as both a manager and
    /// a member is kept as a manager, and each household gets an id and a name.
    /// </summary>
    public static SanitizedHouseholds Sanitize(IEnumerable<Household>? input, Func<Guid, HouseholdUserKind> kindOf)
    {
        var households = new List<Household>();
        var dropped = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var household in input ?? Enumerable.Empty<Household>())
        {
            if (household == null)
            {
                continue;
            }

            if (households.Count >= MaxHouseholds)
            {
                dropped.Add($"Only {MaxHouseholds} households are kept.");
                break;
            }

            var name = (household.Name ?? string.Empty).Trim();
            if (name.Length > MaxNameLength)
            {
                name = name.Substring(0, MaxNameLength);
            }

            var id = ParseId(household.Id)?.ToString("N") ?? string.Empty;
            if (id.Length == 0 || !seenIds.Add(id))
            {
                id = Guid.NewGuid().ToString("N");
                seenIds.Add(id);
            }

            var label = name.Length > 0 ? name : "Household";
            var kept = new HashSet<Guid>();
            var managers = Clean(household.ManagerUserIds, label, kindOf, kept, dropped);
            var members = Clean(household.MemberUserIds, label, kindOf, kept, dropped);

            households.Add(new Household
            {
                Id = id,
                Name = label,
                ManagerUserIds = managers,
                MemberUserIds = members,
            });
        }

        return new SanitizedHouseholds(households, dropped);
    }

    // Managers are cleaned first, so someone in both lists is kept once, as a manager
    private static List<string> Clean(
        IEnumerable<string>? ids,
        string household,
        Func<Guid, HouseholdUserKind> kindOf,
        HashSet<Guid> kept,
        List<string> dropped)
    {
        var result = new List<string>();
        foreach (var raw in ids ?? Enumerable.Empty<string>())
        {
            if (ParseId(raw) is not { } id || kept.Contains(id))
            {
                continue;
            }

            switch (kindOf(id))
            {
                case HouseholdUserKind.Administrator:
                    dropped.Add($"{household}: an administrator can't be in a household.");
                    continue;
                case HouseholdUserKind.Unknown:
                    dropped.Add($"{household}: {id:N} isn't a user on this server.");
                    continue;
            }

            if (kept.Count >= MaxUsersPerHousehold)
            {
                dropped.Add($"{household}: only {MaxUsersPerHousehold} accounts are kept.");
                break;
            }

            kept.Add(id);
            result.Add(id.ToString("N"));
        }

        return result;
    }

    /// <summary>True when <paramref name="userId"/> manages at least one household.</summary>
    public static bool IsManager(IEnumerable<Household>? households, Guid userId) =>
        households?.Any(h => h != null && Ids(h.ManagerUserIds).Contains(userId)) == true;

    /// <summary>
    /// The households <paramref name="manager"/> manages, directly or through another manager
    /// they manage. A manager can sign in as a co-manager, so whatever that co-manager could do
    /// they can do too, and the reach has to follow that.
    /// </summary>
    public static IReadOnlyList<Household> ReachableHouseholds(IReadOnlyList<Household>? households, Guid manager)
    {
        var result = new List<Household>();
        if (households == null || households.Count == 0)
        {
            return result;
        }

        var managers = new HashSet<Guid> { manager };
        var taken = new bool[households.Count];
        var grew = true;
        while (grew)
        {
            grew = false;
            for (var i = 0; i < households.Count; i++)
            {
                var household = households[i];
                if (taken[i] || household == null)
                {
                    continue;
                }

                var ids = Ids(household.ManagerUserIds);
                if (!ids.Any(managers.Contains))
                {
                    continue;
                }

                taken[i] = true;
                result.Add(household);
                foreach (var id in ids)
                {
                    grew |= managers.Add(id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Everyone <paramref name="manager"/> can manage, in the order the households list them,
    /// leaving out the manager themself.
    /// </summary>
    public static IReadOnlyList<Guid> Reach(IReadOnlyList<Household>? households, Guid manager)
    {
        var seen = new HashSet<Guid> { manager };
        var result = new List<Guid>();
        foreach (var household in ReachableHouseholds(households, manager))
        {
            foreach (var id in Ids(household.ManagerUserIds).Concat(Ids(household.MemberUserIds)))
            {
                if (seen.Add(id))
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="manager"/> may act for <paramref name="target"/>. A manager never
    /// acts for themself here: their own PIN and schedule go through the ordinary routes, which
    /// ask for the current PIN.
    /// </summary>
    public static bool CanManage(IReadOnlyList<Household>? households, Guid manager, Guid target) =>
        manager != Guid.Empty && target != Guid.Empty && manager != target && Reach(households, manager).Contains(target);

    private static List<Guid> Ids(IEnumerable<string>? ids)
    {
        var result = new List<Guid>();
        foreach (var raw in ids ?? Enumerable.Empty<string>())
        {
            if (ParseId(raw) is { } id)
            {
                result.Add(id);
            }
        }

        return result;
    }
}
