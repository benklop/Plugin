using System.Reflection;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Moonfin.Households;

namespace Moonfin.Server.Helpers;

/// <summary>
/// Reads and writes a user's policy. The policy's AccessSchedules are entities from Jellyfin.Data
/// up to 10.10 and from Jellyfin.Database.Implementations from 10.11, and the day enum moved with
/// them, so the plugin (built against 10.10) reaches them by name rather than by type.
/// </summary>
internal static class PolicyReflection
{
    private static readonly Type? UserManagerType = Type.GetType("MediaBrowser.Controller.Library.IUserManager, MediaBrowser.Controller");

    private static readonly MethodInfo? GetUserDtoMethod = UserManagerType?
        .GetMethods()
        .Where(m => m.Name == "GetUserDto" && typeof(UserDto).IsAssignableFrom(m.ReturnType))
        .OrderBy(m => m.GetParameters().Length)
        .FirstOrDefault();

    private static readonly MethodInfo? UpdatePolicyMethod = UserManagerType?
        .GetMethod("UpdatePolicyAsync", [typeof(Guid), typeof(UserPolicy)]);

    private static readonly PropertyInfo? SchedulesProperty = typeof(UserPolicy).GetProperty("AccessSchedules");

    /// <summary>The user as the API shows them, policy included, or null when they don't exist.</summary>
    public static UserDto? GetUserDto(IServiceProvider services, Guid userId)
    {
        var userManager = UserReflection.UserManager(services);
        var user = UserReflection.ResolveUser(services, userId);
        if (userManager == null || user == null || GetUserDtoMethod == null)
        {
            return null;
        }

        var args = new object?[GetUserDtoMethod.GetParameters().Length];
        args[0] = user;
        return GetUserDtoMethod.Invoke(userManager, args) as UserDto;
    }

    /// <summary>Whether this server's policy can be read and written here.</summary>
    public static bool CanEditSchedules =>
        UpdatePolicyMethod != null && SchedulesProperty?.PropertyType.IsArray == true && ScheduleConstructor() != null;

    /// <summary>The policy's access schedule entries, or an empty list when it has none.</summary>
    public static List<ScheduleEntry> ReadSchedules(UserPolicy? policy)
    {
        var result = new List<ScheduleEntry>();
        if (policy == null || SchedulesProperty?.GetValue(policy) is not Array entries)
        {
            return result;
        }

        foreach (var entry in entries)
        {
            if (entry == null)
            {
                continue;
            }

            var type = entry.GetType();
            var day = type.GetProperty("DayOfWeek")?.GetValue(entry);
            var start = type.GetProperty("StartHour")?.GetValue(entry);
            var end = type.GetProperty("EndHour")?.GetValue(entry);
            if (day == null || start is not double startHour || end is not double endHour)
            {
                continue;
            }

            result.Add(new ScheduleEntry(Convert.ToInt32(day, System.Globalization.CultureInfo.InvariantCulture), startHour, endHour));
        }

        return result;
    }

    /// <summary>Replaces the policy's access schedule with <paramref name="entries"/>.</summary>
    public static void WriteSchedules(UserPolicy policy, Guid userId, IReadOnlyList<ScheduleEntry> entries)
    {
        var constructor = ScheduleConstructor()
            ?? throw new NotSupportedException("This server's access schedules can't be written by the plugin");
        var elementType = SchedulesProperty!.PropertyType.GetElementType()!;
        var dayType = constructor.GetParameters()[0].ParameterType;

        var array = Array.CreateInstance(elementType, entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            array.SetValue(constructor.Invoke([Enum.ToObject(dayType, entry.DayOfWeek), entry.StartHour, entry.EndHour, userId]), i);
        }

        SchedulesProperty.SetValue(policy, array);
    }

    /// <summary>Saves the whole policy, as the dashboard's user page does.</summary>
    public static async Task UpdatePolicyAsync(IServiceProvider services, Guid userId, UserPolicy policy)
    {
        var userManager = UserReflection.UserManager(services);
        if (userManager == null || UpdatePolicyMethod == null)
        {
            throw new NotSupportedException("This server's user policies can't be written by the plugin");
        }

        try
        {
            if (UpdatePolicyMethod.Invoke(userManager, [userId, policy]) is Task task)
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    // AccessSchedule(DynamicDayOfWeek dayOfWeek, double startHour, double endHour, Guid userId) on every
    // version so far
    private static ConstructorInfo? ScheduleConstructor()
    {
        var elementType = SchedulesProperty?.PropertyType.GetElementType();
        return elementType?.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 4
                && p[0].ParameterType.IsEnum
                && p[1].ParameterType == typeof(double)
                && p[2].ParameterType == typeof(double)
                && p[3].ParameterType == typeof(Guid);
        });
    }
}
