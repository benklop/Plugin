using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using MediaBrowser.Model.Users;
using Moonfin.Households;
using Moonfin.Server.Api;
using Moonfin.Server.Helpers;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class HouseholdServerTests
{
    private static readonly Guid ParentA = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ParentB = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid Kid = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    private sealed class Directory : IUserDirectory
    {
        public List<DirectoryUser> List { get; } = new();

        public IReadOnlyList<DirectoryUser> Users() => List;
    }

    private static (HouseholdService Service, Directory Directory) Home()
    {
        var directory = new Directory();
        directory.List.Add(new DirectoryUser(ParentA, "Parent A", null, false));
        directory.List.Add(new DirectoryUser(ParentB, "Parent B", null, false));
        directory.List.Add(new DirectoryUser(Kid, "Kid", null, false));
        var households = new List<Household>
        {
            new()
            {
                Id = "h1",
                Name = "Home",
                ManagerUserIds = [ParentA.ToString("N"), ParentB.ToString("N")],
                MemberUserIds = [Kid.ToString("N")],
            },
        };
        return (new HouseholdService(directory, () => households), directory);
    }

    [Fact]
    public void Managers_reach_each_other_and_the_kid_as_the_server_lists_them()
    {
        var (service, _) = Home();

        Assert.True(service.IsManager(ParentA));
        Assert.False(service.IsManager(Kid));
        Assert.Equal(["Parent B", "Kid"], service.ManageableBy(ParentA).Select(u => u.Name));
        Assert.NotNull(service.Manageable(ParentB, ParentA));
        Assert.Null(service.Manageable(ParentA, ParentA));
        Assert.Null(service.Manageable(Kid, ParentA));
    }

    [Fact]
    public void Someone_made_an_administrator_after_saving_is_out_of_reach()
    {
        var (service, directory) = Home();
        directory.List[1] = new DirectoryUser(ParentB, "Parent B", null, true);

        Assert.Null(service.Manageable(ParentA, ParentB));
        Assert.Equal(["Kid"], service.ManageableBy(ParentA).Select(u => u.Name));
    }

    [Fact]
    public void A_manager_made_an_administrator_manages_nothing_here()
    {
        var (service, directory) = Home();
        directory.List[0] = new DirectoryUser(ParentA, "Parent A", null, true);

        Assert.False(service.IsManager(ParentA));
        Assert.Empty(service.ManageableBy(ParentA));
    }

    [Fact]
    public void A_deleted_user_is_out_of_reach()
    {
        var (service, directory) = Home();
        directory.List.RemoveAt(2);

        Assert.Null(service.Manageable(ParentA, Kid));
        Assert.Empty(service.ManageableBy(Kid));
    }

    [Fact]
    public void The_kind_lookup_tells_users_administrators_and_strangers_apart()
    {
        var (service, directory) = Home();
        var admin = Guid.NewGuid();
        directory.List.Add(new DirectoryUser(admin, "Admin", null, true));

        var kind = service.KindLookup();

        Assert.Equal(HouseholdUserKind.User, kind(Kid));
        Assert.Equal(HouseholdUserKind.Administrator, kind(admin));
        Assert.Equal(HouseholdUserKind.Unknown, kind(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("10.10.7", false)]
    [InlineData("10.11.9", false)]
    [InlineData("12.0.0", true)]
    [InlineData("12.4.0", true)]
    public void Adding_the_household_needs_jellyfin_12(string version, bool supported)
    {
        Assert.Equal(supported, HouseholdController.ServerSupportsSessions(Version.Parse(version)));
        Assert.False(HouseholdController.ServerSupportsSessions(null));
    }

    [Fact]
    public void Schedules_round_trip_through_the_servers_policy_type()
    {
        Assert.True(PolicyReflection.CanEditSchedules);

        var policy = new UserPolicy();
        var entries = new List<ScheduleEntry>
        {
            new(AccessScheduleRules.Weekday, 15.5, 20),
            new((int)DayOfWeek.Saturday, 8, 22),
        };

        PolicyReflection.WriteSchedules(policy, Kid, entries);

        Assert.Equal(entries, PolicyReflection.ReadSchedules(policy));
        PolicyReflection.WriteSchedules(policy, Kid, []);
        Assert.Empty(PolicyReflection.ReadSchedules(policy));
        Assert.Empty(PolicyReflection.ReadSchedules(null));
    }

    private sealed class Status : IScheduleStatus
    {
        public bool AllowedNow { get; set; }

        public string? NextAllowedUtc { get; set; }

        public string? NextAllowedLocal { get; set; }
    }

    [Fact]
    public void Schedule_status_is_judged_on_the_servers_clock()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test-5", "Test-5");
        var entries = new List<ScheduleEntry> { new(AccessScheduleRules.Everyday, 17, 20) };
        var status = new Status();

        // 14:00Z is 09:00 on the server
        HouseholdController.ApplyScheduleStatus(status, entries, new DateTimeOffset(2026, 11, 2, 14, 0, 0, TimeSpan.Zero), zone);

        Assert.False(status.AllowedNow);
        Assert.Equal("2026-11-02T17:00:00", status.NextAllowedLocal);
        Assert.Equal("2026-11-02T22:00:00Z", status.NextAllowedUtc);

        // 23:00Z is 18:00 on the server
        HouseholdController.ApplyScheduleStatus(status, entries, new DateTimeOffset(2026, 11, 2, 23, 0, 0, TimeSpan.Zero), zone);

        Assert.True(status.AllowedNow);
        Assert.Null(status.NextAllowedLocal);
        Assert.Null(status.NextAllowedUtc);
    }

    [Theory]
    [InlineData("{\"dayOfWeek\":\"Weekend\",\"startHour\":1,\"endHour\":2}", 9)]
    [InlineData("{\"dayOfWeek\":3,\"startHour\":1,\"endHour\":2}", 3)]
    [InlineData("{\"dayOfWeek\":\"Someday\",\"startHour\":1,\"endHour\":2}", null)]
    [InlineData("{\"startHour\":1,\"endHour\":2}", null)]
    public void Schedule_days_are_read_by_name_or_number(string json, int? day)
    {
        var dto = JsonSerializer.Deserialize<ScheduleDto>(json)!;

        Assert.Equal(day, dto.ReadDay());
    }

    [Fact]
    public void Schedule_days_are_written_by_name_like_jellyfin()
    {
        var json = JsonSerializer.Serialize(ScheduleDto.From(new ScheduleEntry(AccessScheduleRules.Weekday, 7, 8.5)));

        Assert.Equal("{\"dayOfWeek\":\"Weekday\",\"startHour\":7,\"endHour\":8.5}", json);
    }

    [Fact]
    public void Households_survive_the_config_file()
    {
        var config = new PluginConfiguration();
        config.Households.Add(new Household
        {
            Id = "h1",
            Name = "Home",
            ManagerUserIds = [ParentA.ToString("N"), ParentB.ToString("N")],
            MemberUserIds = [Kid.ToString("N")],
        });

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, null, 1024, leaveOpen: true))
        using (var textWriter = new XmlTextWriter(writer))
        {
            new XmlSerializer(typeof(PluginConfiguration)).Serialize(textWriter, config);
        }

        stream.Position = 0;
        using var reader = XmlReader.Create(stream);
        var read = (PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader)!;

        var home = Assert.Single(read.Households);
        Assert.Equal("Home", home.Name);
        Assert.Equal(config.Households[0].ManagerUserIds, home.ManagerUserIds);
        Assert.Equal(config.Households[0].MemberUserIds, home.MemberUserIds);
    }
}
