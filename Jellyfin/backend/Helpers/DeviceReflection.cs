using System.Collections;
using System.Reflection;

namespace Moonfin.Server.Helpers;

/// <summary>
/// Looks up the sign-ins (access tokens) Jellyfin holds for a user on a device. The device entity
/// moved from Jellyfin.Data to Jellyfin.Database.Implementations in 10.11, so it is read by name.
/// </summary>
internal static class DeviceReflection
{
    private static readonly Type? DeviceManagerType = Type.GetType("MediaBrowser.Controller.Devices.IDeviceManager, MediaBrowser.Controller");

    // QueryResult<Device> GetDevices(DeviceQuery query), where DeviceQuery has UserId and DeviceId
    private static readonly MethodInfo? GetDevicesMethod = DeviceManagerType?
        .GetMethods()
        .FirstOrDefault(m =>
        {
            if (m.Name != "GetDevices" || m.GetParameters().Length != 1)
            {
                return false;
            }

            var query = m.GetParameters()[0].ParameterType;
            return query.GetProperty("UserId") != null && query.GetProperty("DeviceId") != null && query.GetConstructor(Type.EmptyTypes) != null;
        });

    /// <summary>
    /// The newest access token <paramref name="userId"/> holds on <paramref name="deviceId"/>, or null
    /// when they have none there or it can't be looked up on this server.
    /// </summary>
    public static string? ExistingToken(IServiceProvider services, Guid userId, string deviceId)
    {
        if (GetDevicesMethod == null || DeviceManagerType == null || string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        var manager = services.GetService(DeviceManagerType);
        if (manager == null)
        {
            return null;
        }

        try
        {
            var queryType = GetDevicesMethod.GetParameters()[0].ParameterType;
            var query = Activator.CreateInstance(queryType)!;
            queryType.GetProperty("UserId")!.SetValue(query, userId);
            queryType.GetProperty("DeviceId")!.SetValue(query, deviceId);

            var result = GetDevicesMethod.Invoke(manager, [query]);
            if (result?.GetType().GetProperty("Items")?.GetValue(result) is not IEnumerable items)
            {
                return null;
            }

            string? best = null;
            var bestActivity = DateTime.MinValue;
            foreach (var device in items)
            {
                if (device == null)
                {
                    continue;
                }

                var type = device.GetType();
                // Matched again here in case a version ignores part of the query
                if (type.GetProperty("UserId")?.GetValue(device) is not Guid owner || owner != userId
                    || !string.Equals(type.GetProperty("DeviceId")?.GetValue(device) as string, deviceId, StringComparison.OrdinalIgnoreCase)
                    || type.GetProperty("AccessToken")?.GetValue(device) is not string token || token.Length == 0)
                {
                    continue;
                }

                var activity = type.GetProperty("DateLastActivity")?.GetValue(device) is DateTime last ? last : DateTime.MinValue;
                if (best == null || activity > bestActivity)
                {
                    best = token;
                    bestActivity = activity;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }
}
