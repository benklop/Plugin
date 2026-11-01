using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using Moonfin.Households;
using Moonfin.Pin;

namespace Emby.Plugins.Moonfin.Api
{
    /// <summary>
    /// Lets a household's managers look after the other accounts in it without being
    /// administrators: list them, add them all to the manager's device in one go, reset their PIN
    /// and set their access schedule. Who manages whom is set by an administrator under
    /// Moonfin/Admin/Households. Emby has no Quick Connect, so approving a code for another
    /// device answers that it isn't supported.
    /// </summary>
    public class HouseholdApiService : IService, IRequiresRequest, IHasResultFactory
    {
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> PolicyLocks = new ConcurrentDictionary<Guid, SemaphoreSlim>();

        /// <summary>Sign-ins a manager can start: 30 every 5 minutes, as on Jellyfin.</summary>
        private static readonly ActionRateLimiter SignIns = new ActionRateLimiter(30, TimeSpan.FromMinutes(5));

        private readonly IApplicationHost _appHost;
        private readonly IAuthorizationContext _authContext;
        private readonly ILogger _logger;

        public IRequest Request { get; set; } = null!;
        public IHttpResultFactory ResultFactory { get; set; } = null!;

        public HouseholdApiService(IApplicationHost appHost)
        {
            _appHost = appHost;
            _authContext = appHost.Resolve<IAuthorizationContext>();
            ResultFactory = appHost.Resolve<IHttpResultFactory>();
            _logger = appHost.Resolve<ILogManager>().GetLogger("MoonfinHousehold");
        }

        private object Json(object? body) => MoonfinJson.Result(Request, ResultFactory, body);
        private object Json(int statusCode, object? body) { Request.Response.StatusCode = statusCode; return Json(body); }

        private static IReadOnlyList<Household> Households =>
            (IReadOnlyList<Household>?)Plugin.Instance?.Configuration?.Households ?? Array.Empty<Household>();

        private static User[] AllUsers() =>
            PluginServices.UserManager?.GetUserList(new UserQuery()) ?? Array.Empty<User>();

        // ---------------------------------------------------------------- manager routes

        public object Get(GetHouseholdRequest request)
        {
            var caller = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (caller == null) return NoUser();

            var isManager = IsManager(caller);
            var households = new List<object>();
            var members = new List<object>();
            if (isManager)
            {
                var manageable = ManageableBy(caller);
                var ids = new HashSet<Guid>(manageable.Select(u => u.Id)) { caller.Id };
                foreach (var h in HouseholdRules.ReachableHouseholds(Households, caller.Id))
                {
                    households.Add(new
                    {
                        id = h.Id,
                        name = h.Name,
                        managerIds = h.ManagerUserIds.Where(i => HouseholdRules.ParseId(i) is Guid g && ids.Contains(g)).ToList(),
                        memberIds = h.MemberUserIds.Where(i => HouseholdRules.ParseId(i) is Guid g && ids.Contains(g)).ToList(),
                    });
                }

                var deviceId = CallerAuth()?.ReportedDeviceId;
                var pins = Plugin.Instance?.Pins;
                foreach (var user in manageable)
                {
                    var entries = ReadSchedules(user);
                    var status = Status(entries);
                    members.Add(new
                    {
                        id = user.Id.ToString("N"),
                        name = user.Name,
                        primaryImageTag = (string?)null,
                        isManager = HouseholdRules.IsManager(Households, user.Id),
                        hasPin = pins?.HasPin(user.Id) == true,
                        isDisabled = user.Policy?.IsDisabled == true,
                        enableRemoteAccess = user.Policy?.EnableRemoteAccess != false,
                        signedInOnThisDevice = !string.IsNullOrEmpty(deviceId) && ExistingToken(user, deviceId!) != null,
                        accessSchedules = entries.Select(ScheduleJson).ToList(),
                        allowedNow = status.AllowedNow,
                        nextAllowedUtc = status.NextUtc,
                        nextAllowedLocal = status.NextLocal,
                    });
                }
            }

            return Json(new
            {
                isManager,
                sessionsSupported = true,
                schedulesSupported = true,
                quickConnectEnabled = false,
                households,
                users = members,
            });
        }

        /// <summary>
        /// Signs the household in on the caller's own device and returns each account's token. An
        /// account that already has a sign-in on the device gets that one back.
        /// </summary>
        public async Task<object> Post(CreateHouseholdSessionsRequest request)
        {
            var caller = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (caller == null) return NoUser();
            if (!IsManager(caller)) return Json(403, new { error = "Only a household manager can do this" });

            var auth = CallerAuth();
            if (auth == null || string.IsNullOrEmpty(auth.ReportedDeviceId) || string.IsNullOrEmpty(auth.Client)
                || string.IsNullOrEmpty(auth.Version) || string.IsNullOrEmpty(auth.Device))
            {
                return Json(400, new { error = "This needs the app's device details, which come with a user's own sign-in, not an API key" });
            }

            var wait = SignIns.TryTake(caller.Id);
            if (wait > 0) return TooManyAttempts(wait);

            var body = await MoonfinJson.ReadBodyAsync<HouseholdSessionsBody>(request.RequestStream).ConfigureAwait(false);
            var manageable = ManageableBy(caller);
            var wanted = body?.UserIds is { Count: > 0 } requested
                ? requested.Select(HouseholdRules.ParseId).Where(g => g != null).Select(g => g!.Value).Distinct().ToList()
                : manageable.Select(u => u.Id).ToList();

            var sessions = PluginServices.SessionManager;
            var remote = !IsLocal();
            var results = new List<object>();
            foreach (var id in wanted)
            {
                var target = manageable.FirstOrDefault(u => u.Id == id);
                if (target == null) { results.Add(Failed(id, null, "not_manageable", "You don't manage this account")); continue; }
                if (target.Policy?.IsDisabled == true) { results.Add(Failed(id, target.Name, "disabled", "This account is disabled")); continue; }
                if (remote && target.Policy?.EnableRemoteAccess == false)
                {
                    results.Add(Failed(id, target.Name, "remote_not_allowed", "This account can only be used at home"));
                    continue;
                }

                var status = Status(ReadSchedules(target));
                var existing = ExistingToken(target, auth.ReportedDeviceId);
                if (existing != null)
                {
                    results.Add(Succeeded(target, existing, true, status));
                    _logger.Info("Household manager {0} reused {1}'s sign-in on device {2}", caller.Name, target.Name, auth.Device);
                    continue;
                }

                if (sessions == null) { results.Add(Failed(id, target.Name, "failed", "The session manager isn't available")); continue; }
                try
                {
                    var result = await sessions.CreateNewSession(new AuthenticationRequest
                    {
                        UserId = target.InternalId,
                        App = auth.Client,
                        AppVersion = auth.Version,
                        DeviceId = auth.ReportedDeviceId,
                        DeviceName = auth.Device,
                        RemoteAddress = Request.RemoteIp,
                    }, CancellationToken.None).ConfigureAwait(false);

                    results.Add(Succeeded(target, result.AccessToken, false, status));
                    _logger.Info("Household manager {0} signed {1} in on device {2}", caller.Name, target.Name, auth.Device);
                }
                catch (Exception ex)
                {
                    results.Add(Failed(id, target.Name, "forbidden", ex.Message));
                }
            }

            return Json(new { results });
        }

        public object Post(HouseholdQuickConnectRequest request) =>
            Json(501, new { error = "Emby has no Quick Connect", errorCode = "unsupported_server" });

        public async Task<object> Put(SetHouseholdPinRequest request)
        {
            var (caller, target, refused) = Authorize(request.UserId);
            if (refused != null) return refused;
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();

            var body = await MoonfinJson.ReadBodyAsync<PinBody>(request.RequestStream).ConfigureAwait(false);
            if (!PinService.IsPinShaped(body?.Pin)) return Json(400, new { error = "A PIN is exactly four digits" });

            pins.Set(target!.Id, body!.Pin);
            _logger.Info("Household manager {0} set the PIN for {1}", caller!.Name, target.Name);
            return Json(new { enabled = true });
        }

        public object Delete(RemoveHouseholdPinRequest request)
        {
            var (caller, target, refused) = Authorize(request.UserId);
            if (refused != null) return refused;
            var pins = Plugin.Instance?.Pins;
            if (pins == null) return NotReady();

            pins.Remove(target!.Id);
            _logger.Info("Household manager {0} removed the PIN for {1}", caller!.Name, target.Name);
            return Json(new { enabled = false });
        }

        public object Get(GetHouseholdScheduleRequest request)
        {
            var (_, target, refused) = Authorize(request.UserId);
            if (refused != null) return refused;
            return Json(ScheduleResponse(ReadSchedules(target!)));
        }

        /// <summary>
        /// Replaces the access schedule of an account the caller manages. Only the schedule
        /// changes; the rest of the policy is written back as it was read.
        /// </summary>
        public async Task<object> Put(SetHouseholdScheduleRequest request)
        {
            var (caller, target, refused) = Authorize(request.UserId);
            if (refused != null) return refused;
            var userManager = PluginServices.UserManager;
            if (userManager == null) return NotReady();

            var body = await MoonfinJson.ReadBodyAsync<HouseholdScheduleBody>(request.RequestStream).ConfigureAwait(false);
            var entries = new List<ScheduleEntry>();
            foreach (var item in body?.AccessSchedules ?? new List<HouseholdScheduleEntryBody>())
            {
                var day = ReadDay(item.DayOfWeek);
                if (day == null) return Json(400, new { error = "Each entry needs a day: a weekday name, Everyday, Weekday or Weekend" });
                entries.Add(new ScheduleEntry(day.Value, item.StartHour, item.EndHour));
            }

            var invalid = AccessScheduleRules.Validate(body?.AccessSchedules == null ? null : entries);
            if (invalid != null) return Json(400, new { error = invalid });

            var gate = PolicyLocks.GetOrAdd(target!.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Read as late as possible: the whole policy is written back
                var user = userManager.GetUserById(target.Id);
                if (user == null) return Json(404, new { error = "That user does not exist" });
                var policy = userManager.GetUserPolicy(user);
                if (policy.IsAdministrator) return Json(403, new { error = "An administrator can't be managed" });

                policy.AccessSchedules = entries
                    .Select(e => new AccessSchedule { DayOfWeek = (DynamicDayOfWeek)e.DayOfWeek, StartHour = e.StartHour, EndHour = e.EndHour })
                    .ToArray();
                userManager.UpdateUserPolicy(user.InternalId, policy);
            }
            finally
            {
                gate.Release();
            }

            _logger.Info("Household manager {0} set the access schedule for {1} ({2} entries)", caller!.Name, target.Name, entries.Count);
            return Json(ScheduleResponse(entries));
        }

        // ---------------------------------------------------------------- admin routes

        public object Get(GetAdminHouseholdsRequest request) => Json(AdminResponse(new List<string>()));

        public async Task<object> Put(SetAdminHouseholdsRequest request)
        {
            var plugin = Plugin.Instance;
            if (plugin == null) return NotReady();

            var body = await MoonfinJson.ReadBodyAsync<AdminHouseholdsBody>(request.RequestStream).ConfigureAwait(false);
            var input = (body?.Households ?? new List<HouseholdBody>()).Select(h => new Household
            {
                Id = h.Id ?? string.Empty,
                Name = h.Name ?? string.Empty,
                ManagerUserIds = h.ManagerIds ?? new List<string>(),
                MemberUserIds = h.MemberIds ?? new List<string>(),
            });

            var byId = AllUsers().ToDictionary(u => u.Id);
            var sanitized = HouseholdRules.Sanitize(input, id =>
                !byId.TryGetValue(id, out var user) ? HouseholdUserKind.Unknown
                : user.Policy?.IsAdministrator == true ? HouseholdUserKind.Administrator
                : HouseholdUserKind.User);

            plugin.Configuration.Households = sanitized.Households;
            plugin.SaveConfiguration();
            _logger.Info("Households saved: {0}", sanitized.Households.Count);
            return Json(AdminResponse(sanitized.Dropped));
        }

        private object AdminResponse(List<string> dropped)
        {
            var users = AllUsers();
            var byId = users.ToDictionary(u => u.Id);
            var reach = new Dictionary<string, List<string>>();
            foreach (var managerId in Households.SelectMany(h => h.ManagerUserIds).Select(HouseholdRules.ParseId).Where(g => g != null).Select(g => g!.Value).Distinct())
            {
                reach[managerId.ToString("N")] = byId.TryGetValue(managerId, out var manager)
                    ? ManageableBy(manager).Select(u => u.Name).ToList()
                    : new List<string>();
            }

            return new
            {
                households = Households.Select(h => new { id = h.Id, name = h.Name, managerIds = h.ManagerUserIds, memberIds = h.MemberUserIds }).ToList(),
                users = users
                    .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(u => new { id = u.Id.ToString("N"), name = u.Name, isAdministrator = u.Policy?.IsAdministrator == true })
                    .ToList(),
                reach,
                dropped,
                sessionsSupported = true,
            };
        }

        // ---------------------------------------------------------------- rules

        private static bool IsManager(User user) =>
            user.Policy?.IsAdministrator != true && HouseholdRules.IsManager(Households, user.Id);

        /// <summary>Everyone the manager can reach, as the server lists them now, never administrators.</summary>
        private static List<User> ManageableBy(User manager)
        {
            if (manager.Policy?.IsAdministrator == true) return new List<User>();

            var byId = AllUsers().ToDictionary(u => u.Id);
            var result = new List<User>();
            foreach (var id in HouseholdRules.Reach(Households, manager.Id))
            {
                if (byId.TryGetValue(id, out var user) && user.Policy?.IsAdministrator != true) result.Add(user);
            }

            return result;
        }

        private (User? Caller, User? Target, object? Refused) Authorize(string? targetId)
        {
            var caller = AuthHelpers.GetCurrentUser(Request, _authContext);
            if (caller == null) return (null, null, NoUser());

            var id = HouseholdRules.ParseId(targetId);
            var target = id == null || id == caller.Id ? null : ManageableBy(caller).FirstOrDefault(u => u.Id == id);
            if (target == null) return (caller, null, Json(403, new { error = "You don't manage this account", errorCode = "not_manageable" }));
            return (caller, target, null);
        }

        private AuthorizationInfo? CallerAuth()
        {
            try { return _authContext.GetAuthorizationInfo(Request); }
            catch { return null; }
        }

        /// <summary>The newest active token the user holds on the device, or null.</summary>
        private string? ExistingToken(User user, string reportedDeviceId)
        {
            try
            {
                var repo = _appHost.Resolve<IAuthenticationRepository>();
                var found = repo.Get(new AuthenticationInfoQuery
                {
                    UserId = user.InternalId,
                    ReportedDeviceId = reportedDeviceId,
                    IsActive = true,
                });

                return found?.Items?
                    .Where(i => i != null && i.UserId == user.InternalId && !string.IsNullOrEmpty(i.AccessToken)
                        && string.Equals(i.ReportedDeviceId, reportedDeviceId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.DateLastActivity)
                    .Select(i => i.AccessToken)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not look up {0}'s sign-ins: {1}", user.Name, ex.Message);
                return null;
            }
        }

        // Unknown counts as remote, so an account kept off the internet stays off it
        private bool IsLocal()
        {
            try
            {
                var address = Request.RemoteIp;
                return address != null && _appHost.Resolve<INetworkManager>().IsInLocalNetwork(address);
            }
            catch { return false; }
        }

        private static List<ScheduleEntry> ReadSchedules(User user) =>
            (user.Policy?.AccessSchedules ?? Array.Empty<AccessSchedule>())
                .Where(s => s != null)
                .Select(s => new ScheduleEntry((int)s.DayOfWeek, s.StartHour, s.EndHour))
                .ToList();

        private static int? ReadDay(JsonElement day) => day.ValueKind switch
        {
            JsonValueKind.String => AccessScheduleRules.ParseDay(day.GetString()),
            JsonValueKind.Number when day.TryGetInt32(out var n) => AccessScheduleRules.ParseDay(n.ToString(CultureInfo.InvariantCulture)),
            _ => null,
        };

        private static object ScheduleJson(ScheduleEntry e) =>
            new { dayOfWeek = AccessScheduleRules.DayName(e.DayOfWeek), startHour = e.StartHour, endHour = e.EndHour };

        private readonly struct ScheduleStatus
        {
            public ScheduleStatus(bool allowedNow, string? nextUtc, string? nextLocal)
            {
                AllowedNow = allowedNow;
                NextUtc = nextUtc;
                NextLocal = nextLocal;
            }

            public bool AllowedNow { get; }
            public string? NextUtc { get; }
            public string? NextLocal { get; }
        }

        /// <summary>Whether the account may be used now and when it next may, on the server's clock.</summary>
        private static ScheduleStatus Status(IReadOnlyList<ScheduleEntry> entries)
        {
            var zone = TimeZoneInfo.Local;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;
            var next = AccessScheduleRules.NextAllowed(entries, local);
            return new ScheduleStatus(
                AccessScheduleRules.IsAllowed(entries, local),
                next == null ? null : AccessScheduleRules.ToInstant(next.Value, zone).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                next?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        }

        private static object ScheduleResponse(IReadOnlyList<ScheduleEntry> entries)
        {
            var status = Status(entries);
            return new
            {
                accessSchedules = entries.Select(ScheduleJson).ToList(),
                allowedNow = status.AllowedNow,
                nextAllowedUtc = status.NextUtc,
                nextAllowedLocal = status.NextLocal,
            };
        }

        private object Succeeded(User target, string token, bool reused, ScheduleStatus status) => new
        {
            userId = target.Id.ToString("N"),
            name = target.Name,
            success = true,
            reused,
            accessToken = token,
            serverId = _appHost.SystemId,
            user = PluginServices.UserManager?.GetUserDto(target),
            allowedNow = status.AllowedNow,
            nextAllowedUtc = status.NextUtc,
            nextAllowedLocal = status.NextLocal,
        };

        private static object Failed(Guid id, string? name, string code, string message) =>
            new { userId = id.ToString("N"), name, success = false, errorCode = code, error = message };

        private object TooManyAttempts(int retryAfterSeconds)
        {
            Request.Response.AddHeader("Retry-After", retryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            return Json(429, new { error = "Too many sign-ins, try again shortly", retryAfterSeconds });
        }

        private object NotReady() => Json(503, new { error = "The plugin is not ready" });

        private object NoUser() => Json(401, new { error = "A valid user token is required" });
    }
}
