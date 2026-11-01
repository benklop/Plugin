using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mime;
using System.Security;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moonfin.Households;
using Moonfin.Pin;
using Moonfin.Server.Helpers;
using Moonfin.Server.Services;

namespace Moonfin.Server.Api;

/// <summary>
/// Lets a household's managers look after the other accounts in it without being administrators:
/// list them, add them all to the manager's device in one go, approve a Quick Connect code for
/// one of them on another device, reset their PIN and set their access schedule. Who manages whom
/// is set by an administrator under Moonfin/Admin/Households.
/// </summary>
[ApiController]
[Route("Moonfin")]
[Produces(MediaTypeNames.Application.Json)]
public class HouseholdController : ControllerBase
{
    /// <summary>Sessions and tokens are kept per user on a device from Jellyfin 12.0. Before that,
    /// signing a second user in on a device signed the first one out.</summary>
    internal static readonly Version SessionsMinimumServer = new(12, 0);

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> PolicyLocks = new();

    private readonly HouseholdService _households;
    private readonly PinService _pins;
    private readonly ISessionManager _sessions;
    private readonly INetworkManager _network;
    private readonly ILogger<HouseholdController> _logger;

    public HouseholdController(
        HouseholdService households,
        PinService pins,
        ISessionManager sessions,
        INetworkManager network,
        ILogger<HouseholdController> logger)
    {
        _households = households;
        _pins = pins;
        _sessions = sessions;
        _network = network;
        _logger = logger;
    }

    /// <summary>Whether this server keeps a session per user on a device, which adding the household needs.</summary>
    internal static bool ServerSupportsSessions(Version? serverVersion) =>
        serverVersion != null && serverVersion >= SessionsMinimumServer;

    internal static bool SessionsSupported => ServerSupportsSessions(typeof(ISessionManager).Assembly.GetName().Version);

    /// <summary>
    /// The caller's households and everyone they can manage. Someone who manages nothing gets
    /// empty lists, so a client can ask on sign-in and only offer household setup to managers.
    /// </summary>
    [HttpGet("Household")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<HouseholdResponse> GetHousehold()
    {
        var caller = this.GetUserIdFromClaims();
        if (caller == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        var response = new HouseholdResponse
        {
            IsManager = _households.IsManager(caller.Value),
            SessionsSupported = SessionsSupported,
            SchedulesSupported = PolicyReflection.CanEditSchedules,
            QuickConnectEnabled = QuickConnect()?.IsEnabled == true,
        };

        if (!response.IsManager)
        {
            return Ok(response);
        }

        var manageable = _households.ManageableBy(caller.Value);
        var ids = manageable.Select(u => u.Id).Append(caller.Value).ToHashSet();
        response.Households = HouseholdRules.ReachableHouseholds(_households.Households, caller.Value)
            .Select(h => new HouseholdSummary
            {
                Id = h.Id,
                Name = h.Name,
                ManagerIds = h.ManagerUserIds.Where(id => HouseholdRules.ParseId(id) is { } g && ids.Contains(g)).ToList(),
                MemberIds = h.MemberUserIds.Where(id => HouseholdRules.ParseId(id) is { } g && ids.Contains(g)).ToList(),
            })
            .ToList();

        var deviceId = this.GetDeviceIdFromClaims();
        var sessions = deviceId == null ? new List<SessionInfo>() : _sessions.Sessions.Where(s => s.DeviceId == deviceId).ToList();
        foreach (var user in manageable)
        {
            var dto = PolicyReflection.GetUserDto(HttpContext.RequestServices, user.Id);
            var entries = PolicyReflection.ReadSchedules(dto?.Policy);
            var member = new HouseholdUser
            {
                Id = user.Id.ToString("N"),
                Name = user.Name,
                PrimaryImageTag = user.ImageTag,
                IsManager = _households.IsListedManager(user.Id),
                HasPin = _pins.HasPin(user.Id),
                IsDisabled = dto?.Policy?.IsDisabled == true,
                EnableRemoteAccess = dto?.Policy?.EnableRemoteAccess != false,
                SignedInOnThisDevice = sessions.Any(s => s.UserId == user.Id),
                AccessSchedules = entries.Select(ScheduleDto.From).ToList(),
            };
            ApplyScheduleStatus(member, entries);
            response.Users.Add(member);
        }

        return Ok(response);
    }

    /// <summary>
    /// Signs the household in on the caller's own device, so the app can keep a token for each
    /// account and switch between them. The device is the one the caller's token belongs to, never
    /// one named in the request. Each account gets its own result, so one that can't be signed in
    /// (disabled, not allowed on this device, at its session limit) doesn't stop the others.
    /// </summary>
    [HttpPost("Household/Sessions")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<HouseholdSessionsResponse>> CreateSessions(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] HouseholdSessionsRequest? request)
    {
        var caller = this.GetUserIdFromClaims();
        if (caller == null)
        {
            return Unauthorized(new { error = "A valid user token is required" });
        }

        if (!_households.IsManager(caller.Value))
        {
            return Forbidden("Only a household manager can do this");
        }

        if (!SessionsSupported)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new
            {
                error = "Adding the household to a device needs Jellyfin 12.0 or later, which keeps a session for each account on a device",
                errorCode = "unsupported_server",
            });
        }

        var device = CallerDevice();
        if (device == null)
        {
            return BadRequest(new { error = "This needs the app's device details, which come with a user's own sign-in, not an API key" });
        }

        var wait = _households.SignIns.TryTake(caller.Value);
        if (wait > 0)
        {
            return TooManyAttempts(wait);
        }

        var manageable = _households.ManageableBy(caller.Value);
        List<Guid> wanted;
        if (request?.UserIds is { Count: > 0 } ids)
        {
            wanted = ids.Select(HouseholdRules.ParseId).Where(g => g != null).Select(g => g!.Value).Distinct().ToList();
        }
        else
        {
            wanted = manageable.Select(u => u.Id).ToList();
        }

        var remote = !IsLocal();
        var response = new HouseholdSessionsResponse();
        foreach (var id in wanted)
        {
            var result = new HouseholdSessionResult { UserId = id.ToString("N") };
            response.Results.Add(result);

            var target = manageable.FirstOrDefault(u => u.Id == id);
            if (target == null)
            {
                Fail(result, "not_manageable", "You don't manage this account");
                continue;
            }

            result.Name = target.Name;
            var dto = PolicyReflection.GetUserDto(HttpContext.RequestServices, id);
            if (dto?.Policy == null)
            {
                Fail(result, "not_found", "That user does not exist");
                continue;
            }

            if (dto.Policy.IsAdministrator)
            {
                Fail(result, "not_manageable", "An administrator can't be managed");
                continue;
            }

            if (dto.Policy.IsDisabled)
            {
                Fail(result, "disabled", "This account is disabled");
                continue;
            }

            // Jellyfin's own sign-in turns these away, and the token wouldn't work from here anyway
            if (remote && !dto.Policy.EnableRemoteAccess)
            {
                Fail(result, "remote_not_allowed", "This account can only be used at home");
                continue;
            }

            try
            {
                var auth = await _sessions.AuthenticateDirect(new AuthenticationRequest
                {
                    UserId = id,
                    DeviceId = device.Value.DeviceId,
                    DeviceName = device.Value.DeviceName,
                    App = device.Value.App,
                    AppVersion = device.Value.AppVersion,
                    RemoteEndPoint = HttpContext.Connection.RemoteIpAddress?.ToString(),
                }).ConfigureAwait(false);

                result.Success = true;
                result.AccessToken = auth.AccessToken;
                result.ServerId = auth.ServerId;
                result.User = auth.User;
                var entries = PolicyReflection.ReadSchedules(dto.Policy);
                ApplyScheduleStatus(result, entries);
                _logger.LogInformation(
                    "Household manager {Manager} signed {Target} in on device {Device}",
                    caller.Value.ToString("N"), target.Name, device.Value.DeviceName);
            }
            catch (SecurityException ex)
            {
                Fail(result, "forbidden", ex.Message);
            }
            catch (Exception ex) when (ex is AuthenticationException or ResourceNotFoundException or ArgumentException)
            {
                Fail(result, "failed", ex.Message);
            }
        }

        return Ok(response);
    }

    /// <summary>
    /// Approves a Quick Connect code for an account the caller manages, so the device showing the
    /// code is signed in as that account. The device is signed in even outside the account's
    /// schedule, and Jellyfin then turns its requests away until the schedule allows them; the
    /// response says whether that is the case now.
    /// </summary>
    [HttpPost("Household/Users/{userId}/QuickConnect")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<HouseholdQuickConnectResponse>> AuthorizeQuickConnect([FromRoute] Guid userId, [FromBody] HouseholdQuickConnectRequest request)
    {
        var (caller, target, refused) = Authorize(userId);
        if (refused != null)
        {
            return refused;
        }

        var code = request?.Code?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            return BadRequest(new { error = "A Quick Connect code is required" });
        }

        var quickConnect = QuickConnect();
        if (quickConnect == null || !quickConnect.IsEnabled)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                error = "Quick Connect is turned off on this server",
                errorCode = "quickconnect_disabled",
            });
        }

        var dto = PolicyReflection.GetUserDto(HttpContext.RequestServices, userId);
        if (dto?.Policy == null)
        {
            return NotFound(new { error = "That user does not exist" });
        }

        if (dto.Policy.IsDisabled)
        {
            return Forbidden("This account is disabled", "disabled");
        }

        var wait = _households.SignIns.TryTake(caller);
        if (wait > 0)
        {
            return TooManyAttempts(wait);
        }

        try
        {
            if (!await quickConnect.AuthorizeRequest(userId, code).ConfigureAwait(false))
            {
                return NotFound(new { error = "That code wasn't found or has expired" });
            }
        }
        catch (ResourceNotFoundException)
        {
            return NotFound(new { error = "That code wasn't found or has expired" });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { error = "That code has already been approved" });
        }
        catch (AuthenticationException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Quick Connect is turned off on this server", errorCode = "quickconnect_disabled" });
        }
        catch (SecurityException ex)
        {
            return Forbidden(ex.Message);
        }

        _logger.LogInformation(
            "Household manager {Manager} approved a Quick Connect code for {Target}",
            caller.ToString("N"), target!.Name);

        var response = new HouseholdQuickConnectResponse { Authorized = true, UserId = userId.ToString("N"), Name = target.Name };
        ApplyScheduleStatus(response, PolicyReflection.ReadSchedules(dto.Policy));
        return Ok(response);
    }

    /// <summary>Sets the PIN of an account the caller manages, without its current PIN.</summary>
    [HttpPut("Household/Users/{userId}/Pin")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PinStatusResponse> SetPin([FromRoute] Guid userId, [FromBody] PinRequest request)
    {
        var (caller, target, refused) = Authorize(userId);
        if (refused != null)
        {
            return refused;
        }

        if (!PinService.IsPinShaped(request?.Pin))
        {
            return BadRequest(new { error = "A PIN is exactly four digits" });
        }

        _pins.Set(userId, request!.Pin);
        _logger.LogInformation("Household manager {Manager} set the PIN for {Target}", caller.ToString("N"), target!.Name);
        return Ok(new PinStatusResponse { Enabled = true });
    }

    /// <summary>Removes the PIN of an account the caller manages, so a forgotten one can be reset.</summary>
    [HttpDelete("Household/Users/{userId}/Pin")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<PinStatusResponse> RemovePin([FromRoute] Guid userId)
    {
        var (caller, target, refused) = Authorize(userId);
        if (refused != null)
        {
            return refused;
        }

        _pins.Remove(userId);
        _logger.LogInformation("Household manager {Manager} removed the PIN for {Target}", caller.ToString("N"), target!.Name);
        return Ok(new PinStatusResponse { Enabled = false });
    }

    /// <summary>The access schedule of an account the caller manages, on the server's clock.</summary>
    [HttpGet("Household/Users/{userId}/Schedule")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<HouseholdScheduleResponse> GetSchedule([FromRoute] Guid userId)
    {
        var (_, _, refused) = Authorize(userId);
        if (refused != null)
        {
            return refused;
        }

        var dto = PolicyReflection.GetUserDto(HttpContext.RequestServices, userId);
        if (dto?.Policy == null)
        {
            return NotFound(new { error = "That user does not exist" });
        }

        return Ok(ScheduleResponse(PolicyReflection.ReadSchedules(dto.Policy)));
    }

    /// <summary>
    /// Replaces the access schedule of an account the caller manages. An empty list removes the
    /// schedule, so the account can be used at any time. Only the schedule is changed; the rest of
    /// the account's policy is written back as it was read.
    /// </summary>
    [HttpPut("Household/Users/{userId}/Schedule")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status501NotImplemented)]
    public async Task<ActionResult<HouseholdScheduleResponse>> SetSchedule([FromRoute] Guid userId, [FromBody] HouseholdScheduleRequest request)
    {
        var (caller, target, refused) = Authorize(userId);
        if (refused != null)
        {
            return refused;
        }

        if (!PolicyReflection.CanEditSchedules)
        {
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "This server's access schedules can't be changed by the plugin", errorCode = "unsupported_server" });
        }

        var entries = new List<ScheduleEntry>();
        foreach (var item in request?.AccessSchedules ?? new List<ScheduleDto>())
        {
            var day = item.ReadDay();
            if (day == null)
            {
                return BadRequest(new { error = "Each entry needs a day: a weekday name, Everyday, Weekday or Weekend" });
            }

            entries.Add(new ScheduleEntry(day.Value, item.StartHour, item.EndHour));
        }

        var invalid = AccessScheduleRules.Validate(request?.AccessSchedules == null ? null : entries);
        if (invalid != null)
        {
            return BadRequest(new { error = invalid });
        }

        var gate = PolicyLocks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Read as late as possible: the whole policy is written back, so anything an
            // administrator changed between this read and the write would be lost
            var dto = PolicyReflection.GetUserDto(HttpContext.RequestServices, userId);
            if (dto?.Policy == null)
            {
                return NotFound(new { error = "That user does not exist" });
            }

            if (dto.Policy.IsAdministrator)
            {
                return Forbidden("An administrator can't be managed");
            }

            PolicyReflection.WriteSchedules(dto.Policy, userId, entries);
            await PolicyReflection.UpdatePolicyAsync(HttpContext.RequestServices, userId, dto.Policy).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        _logger.LogInformation(
            "Household manager {Manager} set the access schedule for {Target} ({Count} entries)",
            caller.ToString("N"), target!.Name, entries.Count);
        return Ok(ScheduleResponse(entries));
    }

    /// <summary>The households and, for picking members, every user on the server.</summary>
    [HttpGet("Admin/Households")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AdminHouseholdsResponse> GetAdminHouseholds() => Ok(AdminResponse(new List<string>()));

    /// <summary>
    /// Replaces the households. Administrators and ids that aren't users are left out, and the
    /// response lists what was dropped along with what each manager can now reach.
    /// </summary>
    [HttpPut("Admin/Households")]
    [Authorize(Policy = "RequiresElevation")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<AdminHouseholdsResponse> SetAdminHouseholds([FromBody] AdminHouseholdsRequest request)
    {
        var plugin = MoonfinPlugin.Instance;
        if (plugin == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "The plugin isn't loaded" });
        }

        var input = (request?.Households ?? new List<HouseholdSummary>()).Select(h => new Household
        {
            Id = h.Id ?? string.Empty,
            Name = h.Name ?? string.Empty,
            ManagerUserIds = h.ManagerIds ?? new List<string>(),
            MemberUserIds = h.MemberIds ?? new List<string>(),
        });

        var sanitized = HouseholdRules.Sanitize(input, _households.KindLookup());
        plugin.Configuration.Households = sanitized.Households;
        plugin.SaveConfiguration();
        _logger.LogInformation("Households saved: {Count}", sanitized.Households.Count);
        return Ok(AdminResponse(sanitized.Dropped));
    }

    private AdminHouseholdsResponse AdminResponse(List<string> dropped)
    {
        var directory = HttpContext.RequestServices.GetService(typeof(IUserDirectory)) as IUserDirectory;
        var users = directory?.Users() ?? Array.Empty<DirectoryUser>();
        var households = _households.Households;
        var names = users.ToDictionary(u => u.Id, u => u.Name);
        var managers = households
            .SelectMany(h => h.ManagerUserIds)
            .Select(HouseholdRules.ParseId)
            .Where(g => g != null)
            .Select(g => g!.Value)
            .Distinct();

        return new AdminHouseholdsResponse
        {
            Households = households.Select(h => new HouseholdSummary
            {
                Id = h.Id,
                Name = h.Name,
                ManagerIds = h.ManagerUserIds.ToList(),
                MemberIds = h.MemberUserIds.ToList(),
            }).ToList(),
            Users = users
                .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                .Select(u => new AdminHouseholdUser { Id = u.Id.ToString("N"), Name = u.Name, IsAdministrator = u.IsAdministrator })
                .ToList(),
            Reach = managers.ToDictionary(
                m => m.ToString("N"),
                m => (IReadOnlyList<string>)_households.ManageableBy(m).Select(u => names.TryGetValue(u.Id, out var n) ? n : u.Id.ToString("N")).ToList()),
            Dropped = dropped,
            SessionsSupported = SessionsSupported,
        };
    }

    /// <summary>The caller and the target when the caller manages the target, else the refusal.</summary>
    private (Guid Caller, DirectoryUser? Target, ActionResult? Refused) Authorize(Guid target)
    {
        var caller = this.GetUserIdFromClaims();
        if (caller == null)
        {
            return (Guid.Empty, null, Unauthorized(new { error = "A valid user token is required" }));
        }

        var user = _households.Manageable(caller.Value, target);
        if (user == null)
        {
            return (caller.Value, null, Forbidden("You don't manage this account", "not_manageable"));
        }

        return (caller.Value, user, null);
    }

    private (string DeviceId, string DeviceName, string App, string AppVersion)? CallerDevice()
    {
        var deviceId = this.GetDeviceIdFromClaims();
        var deviceName = User.FindFirst("Jellyfin-Device")?.Value;
        var app = User.FindFirst("Jellyfin-Client")?.Value;
        var version = User.FindFirst("Jellyfin-Version")?.Value;
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(app) || string.IsNullOrEmpty(version))
        {
            return null;
        }

        return (deviceId, deviceName, app, version);
    }

    // Unknown counts as remote, so an account kept off the internet stays off it
    private bool IsLocal()
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address == null)
        {
            return false;
        }

        try
        {
            return _network.IsInLocalNetwork(address);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not tell whether {Address} is local", address);
            return false;
        }
    }

    // Resolved lazily, as the Seerr sign-in does, so a server without it still serves the rest
    private IQuickConnect? QuickConnect() =>
        HttpContext.RequestServices.GetService(typeof(IQuickConnect)) as IQuickConnect;

    private static HouseholdScheduleResponse ScheduleResponse(IReadOnlyList<ScheduleEntry> entries)
    {
        var response = new HouseholdScheduleResponse { AccessSchedules = entries.Select(ScheduleDto.From).ToList() };
        ApplyScheduleStatus(response, entries);
        return response;
    }

    internal static void ApplyScheduleStatus(IScheduleStatus status, IReadOnlyList<ScheduleEntry> entries) =>
        ApplyScheduleStatus(status, entries, DateTimeOffset.UtcNow, TimeZoneInfo.Local);

    /// <summary>Fills in whether the account is open now and when it next opens, for a clock and zone.</summary>
    internal static void ApplyScheduleStatus(IScheduleStatus status, IReadOnlyList<ScheduleEntry> entries, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        status.AllowedNow = AccessScheduleRules.IsAllowed(entries, local);
        var next = AccessScheduleRules.NextAllowed(entries, local);
        if (next is { } opens)
        {
            status.NextAllowedLocal = opens.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            status.NextAllowedUtc = AccessScheduleRules.ToInstant(opens, zone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }
        else
        {
            status.NextAllowedLocal = null;
            status.NextAllowedUtc = null;
        }
    }

    private static void Fail(HouseholdSessionResult result, string code, string message)
    {
        result.Success = false;
        result.ErrorCode = code;
        result.Error = message;
    }

    private ObjectResult Forbidden(string error, string? errorCode = null) =>
        StatusCode(StatusCodes.Status403Forbidden, errorCode == null ? new { error } : new { error, errorCode });

    private ObjectResult TooManyAttempts(int retryAfterSeconds)
    {
        Response.Headers["Retry-After"] = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new { error = "Too many sign-ins, try again shortly", retryAfterSeconds });
    }
}

/// <summary>Whether an account may be used now and when it next may, on responses that carry it.</summary>
public interface IScheduleStatus
{
    bool AllowedNow { get; set; }

    string? NextAllowedUtc { get; set; }

    string? NextAllowedLocal { get; set; }
}

public class ScheduleDto
{
    /// <summary>Jellyfin's name for the day, or its number on the way in.</summary>
    [JsonPropertyName("dayOfWeek")]
    public JsonElement DayOfWeek { get; set; }

    [JsonPropertyName("startHour")]
    public double StartHour { get; set; }

    [JsonPropertyName("endHour")]
    public double EndHour { get; set; }

    internal int? ReadDay() => DayOfWeek.ValueKind switch
    {
        JsonValueKind.String => AccessScheduleRules.ParseDay(DayOfWeek.GetString()),
        JsonValueKind.Number when DayOfWeek.TryGetInt32(out var n) => AccessScheduleRules.ParseDay(n.ToString(CultureInfo.InvariantCulture)),
        _ => null,
    };

    internal static ScheduleDto From(ScheduleEntry entry) => new()
    {
        DayOfWeek = JsonSerializer.SerializeToElement(AccessScheduleRules.DayName(entry.DayOfWeek)),
        StartHour = entry.StartHour,
        EndHour = entry.EndHour,
    };
}

public class HouseholdResponse
{
    [JsonPropertyName("isManager")]
    public bool IsManager { get; set; }

    /// <summary>Whether Household/Sessions works on this server (Jellyfin 12.0 or later).</summary>
    [JsonPropertyName("sessionsSupported")]
    public bool SessionsSupported { get; set; }

    [JsonPropertyName("schedulesSupported")]
    public bool SchedulesSupported { get; set; }

    [JsonPropertyName("quickConnectEnabled")]
    public bool QuickConnectEnabled { get; set; }

    [JsonPropertyName("households")]
    public List<HouseholdSummary> Households { get; set; } = new();

    [JsonPropertyName("users")]
    public List<HouseholdUser> Users { get; set; } = new();
}

public class HouseholdSummary
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("managerIds")]
    public List<string>? ManagerIds { get; set; } = new();

    [JsonPropertyName("memberIds")]
    public List<string>? MemberIds { get; set; } = new();
}

public class HouseholdUser : IScheduleStatus
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("primaryImageTag")]
    public string? PrimaryImageTag { get; set; }

    [JsonPropertyName("isManager")]
    public bool IsManager { get; set; }

    [JsonPropertyName("hasPin")]
    public bool HasPin { get; set; }

    [JsonPropertyName("isDisabled")]
    public bool IsDisabled { get; set; }

    [JsonPropertyName("enableRemoteAccess")]
    public bool EnableRemoteAccess { get; set; }

    /// <summary>Whether the account has a live session on the device asking.</summary>
    [JsonPropertyName("signedInOnThisDevice")]
    public bool SignedInOnThisDevice { get; set; }

    [JsonPropertyName("accessSchedules")]
    public List<ScheduleDto> AccessSchedules { get; set; } = new();

    [JsonPropertyName("allowedNow")]
    public bool AllowedNow { get; set; }

    [JsonPropertyName("nextAllowedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedUtc { get; set; }

    [JsonPropertyName("nextAllowedLocal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedLocal { get; set; }
}

public class HouseholdSessionsRequest
{
    /// <summary>Which accounts to sign in. Leave it out for everyone the caller manages.</summary>
    [JsonPropertyName("userIds")]
    public List<string>? UserIds { get; set; }
}

public class HouseholdSessionsResponse
{
    [JsonPropertyName("results")]
    public List<HouseholdSessionResult> Results { get; set; } = new();
}

public class HouseholdSessionResult : IScheduleStatus
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("accessToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccessToken { get; set; }

    [JsonPropertyName("serverId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServerId { get; set; }

    /// <summary>The user as Jellyfin's own sign-in returns them.</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UserDto? User { get; set; }

    [JsonPropertyName("allowedNow")]
    public bool AllowedNow { get; set; }

    [JsonPropertyName("nextAllowedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedUtc { get; set; }

    [JsonPropertyName("nextAllowedLocal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedLocal { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }
}

public class HouseholdQuickConnectRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public class HouseholdQuickConnectResponse : IScheduleStatus
{
    [JsonPropertyName("authorized")]
    public bool Authorized { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("allowedNow")]
    public bool AllowedNow { get; set; }

    [JsonPropertyName("nextAllowedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedUtc { get; set; }

    [JsonPropertyName("nextAllowedLocal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedLocal { get; set; }
}

public class HouseholdScheduleRequest
{
    /// <summary>The whole schedule. An empty list removes it.</summary>
    [JsonPropertyName("accessSchedules")]
    public List<ScheduleDto>? AccessSchedules { get; set; }
}

public class HouseholdScheduleResponse : IScheduleStatus
{
    [JsonPropertyName("accessSchedules")]
    public List<ScheduleDto> AccessSchedules { get; set; } = new();

    [JsonPropertyName("allowedNow")]
    public bool AllowedNow { get; set; }

    [JsonPropertyName("nextAllowedUtc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedUtc { get; set; }

    [JsonPropertyName("nextAllowedLocal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextAllowedLocal { get; set; }
}

public class AdminHouseholdsRequest
{
    [JsonPropertyName("households")]
    public List<HouseholdSummary>? Households { get; set; }
}

public class AdminHouseholdsResponse
{
    [JsonPropertyName("households")]
    public List<HouseholdSummary> Households { get; set; } = new();

    [JsonPropertyName("users")]
    public List<AdminHouseholdUser> Users { get; set; } = new();

    /// <summary>For each manager, the names of everyone they can manage, other households included.</summary>
    [JsonPropertyName("reach")]
    public Dictionary<string, IReadOnlyList<string>> Reach { get; set; } = new();

    /// <summary>Why ids were left out of the last save.</summary>
    [JsonPropertyName("dropped")]
    public List<string> Dropped { get; set; } = new();

    [JsonPropertyName("sessionsSupported")]
    public bool SessionsSupported { get; set; }
}

public class AdminHouseholdUser
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isAdministrator")]
    public bool IsAdministrator { get; set; }
}
