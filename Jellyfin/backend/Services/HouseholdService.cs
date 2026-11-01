using Moonfin.Households;

namespace Moonfin.Server.Services;

/// <summary>
/// Who may manage whom, from the households in the plugin config. Every answer is checked against
/// the server's users as they are now, so someone made an administrator after being put in a
/// household, or since deleted, is never reachable.
/// </summary>
public class HouseholdService
{
    private readonly IUserDirectory _directory;
    private readonly Func<IReadOnlyList<Household>> _households;

    public HouseholdService(IUserDirectory directory)
        : this(directory, () => MoonfinPlugin.Instance?.Configuration.Households ?? new List<Household>())
    {
    }

    public HouseholdService(IUserDirectory directory, Func<IReadOnlyList<Household>> households)
    {
        _directory = directory;
        _households = households;
    }

    /// <summary>Sign-in actions a manager can take: minting sessions and approving codes.</summary>
    public ActionRateLimiter SignIns { get; } = new(30, TimeSpan.FromMinutes(5));

    public IReadOnlyList<Household> Households => _households();

    /// <summary>True when <paramref name="userId"/> manages a household and isn't an administrator.</summary>
    public bool IsManager(Guid userId)
    {
        if (!HouseholdRules.IsManager(Households, userId))
        {
            return false;
        }

        var user = _directory.Users().FirstOrDefault(u => u.Id == userId);
        return user is { IsAdministrator: false };
    }

    /// <summary>Everyone <paramref name="manager"/> can manage, as the server lists them now.</summary>
    public IReadOnlyList<DirectoryUser> ManageableBy(Guid manager)
    {
        var users = _directory.Users();
        var self = users.FirstOrDefault(u => u.Id == manager);
        if (self == null || self.IsAdministrator)
        {
            return Array.Empty<DirectoryUser>();
        }

        var byId = users.ToDictionary(u => u.Id);
        var result = new List<DirectoryUser>();
        foreach (var id in HouseholdRules.Reach(Households, manager))
        {
            if (byId.TryGetValue(id, out var user) && !user.IsAdministrator)
            {
                result.Add(user);
            }
        }

        return result;
    }

    /// <summary>The user <paramref name="manager"/> may act for, or null when they may not.</summary>
    public DirectoryUser? Manageable(Guid manager, Guid target) =>
        target == manager ? null : ManageableBy(manager).FirstOrDefault(u => u.Id == target);

    /// <summary>Whether <paramref name="userId"/> is a manager in any household, for listing.</summary>
    public bool IsListedManager(Guid userId) => HouseholdRules.IsManager(Households, userId);

    /// <summary>What the server knows about each id when households are saved.</summary>
    public Func<Guid, HouseholdUserKind> KindLookup()
    {
        var byId = _directory.Users().ToDictionary(u => u.Id);
        return id => !byId.TryGetValue(id, out var user)
            ? HouseholdUserKind.Unknown
            : user.IsAdministrator ? HouseholdUserKind.Administrator : HouseholdUserKind.User;
    }
}
