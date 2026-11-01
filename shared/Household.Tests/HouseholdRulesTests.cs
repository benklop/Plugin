using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Moonfin.Households.Tests;

public class HouseholdRulesTests
{
    private static readonly Guid ParentA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ParentB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid Kid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid Grandma = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid Cousin = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
    private static readonly Guid Stranger = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006");
    private static readonly Guid Admin = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000ad");

    private static Household Make(string name, IEnumerable<Guid> managers, IEnumerable<Guid> members) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        ManagerUserIds = managers.Select(m => m.ToString("N")).ToList(),
        MemberUserIds = members.Select(m => m.ToString("N")).ToList(),
    };

    private static HouseholdUserKind Kind(Guid id) =>
        id == Admin ? HouseholdUserKind.Administrator
        : id == Stranger ? HouseholdUserKind.Unknown
        : HouseholdUserKind.User;

    [Fact]
    public void Two_parents_manage_each_other_and_the_kids()
    {
        var households = new List<Household> { Make("Home", [ParentA, ParentB], [Kid]) };

        Assert.True(HouseholdRules.CanManage(households, ParentA, ParentB));
        Assert.True(HouseholdRules.CanManage(households, ParentB, ParentA));
        Assert.True(HouseholdRules.CanManage(households, ParentA, Kid));
        Assert.Equal([ParentB, Kid], HouseholdRules.Reach(households, ParentA));
    }

    [Fact]
    public void A_member_manages_nobody()
    {
        var households = new List<Household> { Make("Home", [ParentA], [Kid, ParentB]) };

        Assert.False(HouseholdRules.IsManager(households, Kid));
        Assert.False(HouseholdRules.CanManage(households, Kid, ParentA));
        Assert.Empty(HouseholdRules.Reach(households, Kid));
    }

    [Fact]
    public void A_manager_does_not_manage_themself()
    {
        var households = new List<Household> { Make("Home", [ParentA], [Kid]) };

        Assert.False(HouseholdRules.CanManage(households, ParentA, ParentA));
        Assert.DoesNotContain(ParentA, HouseholdRules.Reach(households, ParentA));
    }

    [Fact]
    public void Reach_follows_a_co_manager_into_their_other_household()
    {
        // ParentA can sign in as ParentB, and ParentB manages Grandma's household too
        var households = new List<Household>
        {
            Make("Home", [ParentA, ParentB], [Kid]),
            Make("Grandma's", [ParentB, Grandma], [Cousin]),
        };

        var reach = HouseholdRules.Reach(households, ParentA);

        Assert.Equal([ParentB, Kid, Grandma, Cousin], reach);
        Assert.True(HouseholdRules.CanManage(households, ParentA, Cousin));
        Assert.Equal(2, HouseholdRules.ReachableHouseholds(households, ParentA).Count);
    }

    [Fact]
    public void Reach_does_not_follow_a_plain_member_into_their_other_household()
    {
        var households = new List<Household>
        {
            Make("Home", [ParentA], [Kid]),
            Make("Friends", [Grandma], [Kid, Cousin]),
        };

        Assert.False(HouseholdRules.CanManage(households, ParentA, Cousin));
        Assert.False(HouseholdRules.CanManage(households, ParentA, Grandma));
        Assert.Single(HouseholdRules.ReachableHouseholds(households, ParentA));
    }

    [Fact]
    public void Reach_follows_chains_listed_out_of_order()
    {
        var households = new List<Household>
        {
            Make("Third", [Grandma], [Cousin]),
            Make("Second", [ParentB, Grandma], []),
            Make("First", [ParentA, ParentB], []),
        };

        Assert.True(HouseholdRules.CanManage(households, ParentA, Cousin));
    }

    [Fact]
    public void Nothing_is_reachable_without_households_or_with_empty_ids()
    {
        Assert.False(HouseholdRules.CanManage(null, ParentA, Kid));
        Assert.False(HouseholdRules.CanManage(new List<Household>(), ParentA, Kid));

        var households = new List<Household> { Make("Home", [ParentA], [Kid]) };
        Assert.False(HouseholdRules.CanManage(households, Guid.Empty, Kid));
        Assert.False(HouseholdRules.CanManage(households, ParentA, Guid.Empty));
    }

    [Fact]
    public void Ids_in_any_guid_format_are_matched()
    {
        var households = new List<Household>
        {
            new() { ManagerUserIds = [ParentA.ToString("D").ToUpperInvariant()], MemberUserIds = [Kid.ToString("B")] },
        };

        Assert.True(HouseholdRules.CanManage(households, ParentA, Kid));
    }

    [Fact]
    public void Sanitize_drops_administrators_and_unknown_users()
    {
        var result = HouseholdRules.Sanitize([Make("Home", [ParentA, Admin], [Kid, Stranger, Admin])], Kind);

        var home = Assert.Single(result.Households);
        Assert.Equal([ParentA.ToString("N")], home.ManagerUserIds);
        Assert.Equal([Kid.ToString("N")], home.MemberUserIds);
        Assert.Equal(3, result.Dropped.Count);
    }

    [Fact]
    public void Sanitize_keeps_someone_listed_twice_once_as_a_manager()
    {
        var household = Make("Home", [ParentA, ParentA], [ParentA, Kid, Kid]);

        var home = Assert.Single(HouseholdRules.Sanitize([household], Kind).Households);

        Assert.Equal([ParentA.ToString("N")], home.ManagerUserIds);
        Assert.Equal([Kid.ToString("N")], home.MemberUserIds);
    }

    [Fact]
    public void Sanitize_normalizes_ids_and_names()
    {
        var household = new Household
        {
            Id = "not a guid",
            Name = "   ",
            ManagerUserIds = [ParentA.ToString("D"), "junk", ""],
            MemberUserIds = [Kid.ToString("B")],
        };

        var home = Assert.Single(HouseholdRules.Sanitize([household], Kind).Households);

        Assert.NotNull(HouseholdRules.ParseId(home.Id));
        Assert.Equal("Household", home.Name);
        Assert.Equal([ParentA.ToString("N")], home.ManagerUserIds);
        Assert.Equal([Kid.ToString("N")], home.MemberUserIds);
    }

    [Fact]
    public void Sanitize_gives_duplicate_household_ids_a_new_one()
    {
        var first = Make("One", [ParentA], []);
        var second = Make("Two", [ParentB], []);
        second.Id = first.Id;

        var saved = HouseholdRules.Sanitize([first, second], Kind).Households;

        Assert.Equal(first.Id, saved[0].Id);
        Assert.NotEqual(saved[0].Id, saved[1].Id);
    }

    [Fact]
    public void Sanitize_caps_the_number_of_households()
    {
        var many = Enumerable.Range(0, HouseholdRules.MaxHouseholds + 5).Select(i => Make("H" + i, [ParentA], []));

        var result = HouseholdRules.Sanitize(many, Kind);

        Assert.Equal(HouseholdRules.MaxHouseholds, result.Households.Count);
        Assert.NotEmpty(result.Dropped);
    }

    [Fact]
    public void Sanitize_tolerates_null_input()
    {
        Assert.Empty(HouseholdRules.Sanitize(null, Kind).Households);
    }
}
