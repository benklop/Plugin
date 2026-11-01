using System.Collections.Generic;
using System.Text.Json;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace Emby.Plugins.Moonfin.Api
{
    [Route("/Moonfin/Household", "GET")]
    [Authenticated]
    public class GetHouseholdRequest : IReturn<object> { }

    [Route("/Moonfin/Household/Sessions", "POST")]
    [Authenticated]
    public class CreateHouseholdSessionsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Household/Users/{UserId}/QuickConnect", "POST")]
    [Authenticated]
    public class HouseholdQuickConnectRequest : IReturn<object>
    {
        public string? UserId { get; set; }
    }

    [Route("/Moonfin/Household/Users/{UserId}/Pin", "PUT")]
    [Authenticated]
    public class SetHouseholdPinRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? UserId { get; set; }

        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Household/Users/{UserId}/Pin", "DELETE")]
    [Authenticated]
    public class RemoveHouseholdPinRequest : IReturn<object>
    {
        public string? UserId { get; set; }
    }

    [Route("/Moonfin/Household/Users/{UserId}/Schedule", "GET")]
    [Authenticated]
    public class GetHouseholdScheduleRequest : IReturn<object>
    {
        public string? UserId { get; set; }
    }

    [Route("/Moonfin/Household/Users/{UserId}/Schedule", "PUT")]
    [Authenticated]
    public class SetHouseholdScheduleRequest : IReturn<object>, IRequiresRequestStream
    {
        public string? UserId { get; set; }

        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    [Route("/Moonfin/Admin/Households", "GET")]
    [Authenticated(Roles = "Admin")]
    public class GetAdminHouseholdsRequest : IReturn<object> { }

    [Route("/Moonfin/Admin/Households", "PUT")]
    [Authenticated(Roles = "Admin")]
    public class SetAdminHouseholdsRequest : IReturn<object>, IRequiresRequestStream
    {
        public System.IO.Stream RequestStream { get; set; } = null!;
    }

    /// <summary>Body for Household/Sessions. Leave UserIds out for everyone the caller manages.</summary>
    public class HouseholdSessionsBody
    {
        public List<string>? UserIds { get; set; }
    }

    /// <summary>Body for a schedule. An empty list removes it.</summary>
    public class HouseholdScheduleBody
    {
        public List<HouseholdScheduleEntryBody>? AccessSchedules { get; set; }
    }

    public class HouseholdScheduleEntryBody
    {
        /// <summary>The day's name, as Jellyfin and Emby write it, or its number.</summary>
        public JsonElement DayOfWeek { get; set; }

        public double StartHour { get; set; }

        public double EndHour { get; set; }
    }

    /// <summary>A household as the admin page sends it.</summary>
    public class HouseholdBody
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public List<string>? ManagerIds { get; set; }

        public List<string>? MemberIds { get; set; }
    }

    public class AdminHouseholdsBody
    {
        public List<HouseholdBody>? Households { get; set; }
    }
}
