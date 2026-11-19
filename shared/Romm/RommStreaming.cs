using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>On a RomM game's detail: the game can stream from one of RomM's emulator containers.</summary>
public sealed class RommStreamOffer
{
    /// <summary>RomM's play button text for the platform, like DuckStation or RA PPSSPP.</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    /// <summary>The user's RomM link predates streaming, so they link again once to stream.</summary>
    [JsonPropertyName("needsRelink")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NeedsRelink { get; set; }
}

/// <summary>A started stream. The app hands <see cref="SessionId"/> back on every later call.</summary>
public sealed class RommStreamSession
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public sealed class RommStreamState
{
    /// <summary>starting, ready or ended.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "starting";

    /// <summary>What RomM is doing before launch, such as unpacking a large game.</summary>
    [JsonPropertyName("phase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phase { get; set; }

    /// <summary>The room to open once ready. It carries a token that only works for this session.</summary>
    [JsonPropertyName("roomUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoomUrl { get; set; }
}

/// <summary>Why a stream can't start or continue, as the apps see it.</summary>
public sealed class RommStreamError
{
    /// <summary>
    /// busy (every container in use), alreadyPlaying (the user streams this platform elsewhere),
    /// needsRelink, notLinked, unavailable (no container, or no streaming grant), needsCardImport
    /// (start the game once from RomM's own player), unreachable.
    /// </summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    /// <summary>The game holding the container, when RomM lets the user see it.</summary>
    [JsonPropertyName("romName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RomName { get; set; }
}

public sealed class RommStreamException : Exception
{
    public RommStreamException(int statusCode, string error, string? romName = null)
        : base(error)
    {
        StatusCode = statusCode;
        Body = new RommStreamError { Error = error, RomName = romName };
    }

    public int StatusCode { get; }

    public RommStreamError Body { get; }
}

/// <summary>
/// Streams RomM games from RomM's own emulator containers, always with the user's own RomM token:
/// RomM's streaming grant, saves and play time all belong to that user. The plugin only starts,
/// keeps and ends sessions. The video goes straight from RomM's container to the app.
/// </summary>
public sealed class RommStreaming
{
    /// <summary>The first RomM release with the claim, status and heartbeat API used here.</summary>
    public static readonly Version MinimumVersion = new Version(5, 3, 0);

    private static readonly TimeSpan ConfigLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan VersionLifetime = TimeSpan.FromMinutes(10);

    private readonly RommSource _source;
    private readonly Func<HttpClient> _http;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<string, Cached<RommStreamingConfig?>> _configs = new ConcurrentDictionary<string, Cached<RommStreamingConfig?>>();
    private Cached<bool>? _versionOk;

    public RommStreaming(RommSource source, Func<HttpClient> http, Func<DateTime>? clock = null)
    {
        _source = source;
        _http = http;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// What a game's detail says about streaming for this user, or null when it can't stream:
    /// the user isn't linked, RomM is too old or off, the user lacks RomM's streaming grant, or
    /// no container serves the platform.
    /// </summary>
    public async Task<RommStreamOffer?> OfferAsync(Guid? userId, RommPlatform platform, CancellationToken cancellationToken)
    {
        var token = userId == null ? null : _source.Accounts?.TokenFor(userId.Value);
        if (token == null)
        {
            return null;
        }

        var row = await PlatformRowAsync(token, platform, cancellationToken).ConfigureAwait(false);
        if (row == null)
        {
            return null;
        }

        return _source.Accounts!.CanStream(userId!.Value)
            ? new RommStreamOffer { Label = row.Label }
            : new RommStreamOffer { NeedsRelink = true };
    }

    public async Task<RommStreamSession> StartAsync(Guid userId, RommGame game, RommPlatform platform, CancellationToken cancellationToken)
    {
        var client = ClientFor(userId);
        RommStreamClaim claim;
        try
        {
            claim = await client.ClaimStreamAsync(game.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (RommRequestException e)
        {
            throw MapClaimFailure(e);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            throw new RommStreamException(502, "unreachable");
        }

        // RomM claimed for the ROM's platform, which is the one the game was listed under.
        if (!string.Equals(claim.Platform, platform.Slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new RommStreamException(502, "unreachable");
        }

        return new RommStreamSession
        {
            SessionId = EncodeSessionId(claim.Platform, claim.Container, claim.ClaimedAt),
            Label = claim.Label,
        };
    }

    public Task<RommStreamState> StatusAsync(Guid userId, RommPlatform platform, string sessionId, CancellationToken cancellationToken) =>
        SessionCallAsync(userId, platform, sessionId, (client, s) => client.GetStreamStatusAsync(s.Platform, s.ClaimedAt, cancellationToken));

    public Task<RommStreamState> HeartbeatAsync(Guid userId, RommPlatform platform, string sessionId, CancellationToken cancellationToken) =>
        SessionCallAsync(userId, platform, sessionId, (client, s) => client.HeartbeatStreamAsync(s.Platform, s.Container, s.ClaimedAt, cancellationToken));

    /// <summary>Saves and ends the session. One that already ended counts as ended.</summary>
    public async Task ExitAsync(Guid userId, RommPlatform platform, string sessionId, CancellationToken cancellationToken)
    {
        var session = DecodeSessionId(sessionId, platform);
        try
        {
            await ClientFor(userId).ExitStreamAsync(session.Platform, session.Container, session.ClaimedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (RommRequestException e) when (e.StatusCode == 404)
        {
        }
        catch (RommRequestException e) when (e.StatusCode == 401 || e.StatusCode == 403)
        {
            throw AccessFailure(e);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            throw new RommStreamException(502, "unreachable");
        }
    }

    /// <summary>
    /// The streaming line for the admin page's test: the platforms RomM streams, as the admin
    /// token sees them. Null when RomM is too old for Moonfin's streaming or has it off.
    /// </summary>
    public static async Task<string[]?> PlatformsForTestAsync(RommClient client, string? version, CancellationToken cancellationToken)
    {
        if (!IsSupportedVersion(version))
        {
            return null;
        }

        try
        {
            var config = await client.GetStreamingConfigAsync(cancellationToken).ConfigureAwait(false);
            return config.Enabled && config.Platforms.Count > 0 ? config.Platforms.Keys.ToArray() : null;
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            return null;
        }
    }

    /// <summary>
    /// A version RomM reported, held to <see cref="MinimumVersion"/>. One that doesn't parse, such
    /// as a development build, is let through: the calls themselves then decide.
    /// </summary>
    public static bool IsSupportedVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var core = version!.Trim().TrimStart('v', 'V');
        var end = 0;
        while (end < core.Length && (char.IsDigit(core[end]) || core[end] == '.'))
        {
            end++;
        }

        return !Version.TryParse(core.Substring(0, end).TrimEnd('.'), out var parsed) || parsed >= MinimumVersion;
    }

    /// <summary>
    /// The room's address for the app. RomM gives an absolute URL, or a path when the container
    /// is reverse proxied onto RomM's own origin. Anything else is refused.
    /// </summary>
    public static string? RoomUrl(string baseUrl, string? host)
    {
        if (string.IsNullOrWhiteSpace(host) ||
            !Uri.TryCreate(new Uri(baseUrl.TrimEnd('/') + "/"), host, out var room) ||
            (room.Scheme != Uri.UriSchemeHttp && room.Scheme != Uri.UriSchemeHttps) ||
            room.UserInfo.Length > 0)
        {
            return null;
        }

        return room.AbsoluteUri;
    }

    internal static string EncodeSessionId(string platform, string container, string claimedAt) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(platform + "\n" + container + "\n" + claimedAt))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static (string Platform, string Container, string ClaimedAt) DecodeSessionId(string? sessionId, RommPlatform platform)
    {
        string[]? parts = null;
        try
        {
            var text = (sessionId ?? string.Empty).Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
            parts = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Split('\n');
        }
        catch (FormatException)
        {
        }

        // A session id names its platform, so it can't steer calls for a game on another one.
        if (parts == null || parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0 ||
            !string.Equals(parts[0], platform.Slug, StringComparison.OrdinalIgnoreCase))
        {
            throw new RommStreamException(400, "unavailable");
        }

        return (parts[0], parts[1], parts[2]);
    }

    private async Task<RommStreamState> SessionCallAsync(
        Guid userId,
        RommPlatform platform,
        string sessionId,
        Func<RommClient, (string Platform, string Container, string ClaimedAt), Task<RommStreamStatus>> call)
    {
        var session = DecodeSessionId(sessionId, platform);
        RommStreamStatus status;
        try
        {
            status = await call(ClientFor(userId), session).ConfigureAwait(false);
        }
        catch (RommRequestException e) when (e.StatusCode == 404)
        {
            return new RommStreamState { State = "ended" };
        }
        catch (RommRequestException e) when (e.StatusCode == 401 || e.StatusCode == 403)
        {
            throw AccessFailure(e);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e))
        {
            throw new RommStreamException(502, "unreachable");
        }

        if (!status.Active)
        {
            return new RommStreamState { State = "ended" };
        }

        var room = RoomUrl(_source.Settings?.BaseUrl ?? string.Empty, status.Host);
        return room == null
            ? new RommStreamState { State = "starting", Phase = status.Phase }
            : new RommStreamState { State = "ready", RoomUrl = room };
    }

    private RommClient ClientFor(Guid userId)
    {
        var settings = _source.Settings;
        var accounts = _source.Accounts;
        var token = accounts?.TokenFor(userId);
        if (settings == null || token == null)
        {
            throw new RommStreamException(403, "notLinked");
        }

        if (!accounts!.CanStream(userId))
        {
            throw new RommStreamException(403, "needsRelink");
        }

        return new RommClient(_http(), settings.BaseUrl, token);
    }

    private static RommStreamException MapClaimFailure(RommRequestException e)
    {
        switch (e.StatusCode)
        {
            case 401:
            case 403:
                return AccessFailure(e);
            case 404:
                return new RommStreamException(404, "unavailable");
            case 409:
                var detail = Detail(e.Body);
                var mine = detail.Message?.StartsWith("You already have", StringComparison.OrdinalIgnoreCase) == true;
                return new RommStreamException(409, mine ? "alreadyPlaying" : "busy", detail.RomName);
            case 428:
                return new RommStreamException(409, "needsCardImport");
            default:
                return new RommStreamException(502, "unreachable");
        }
    }

    // A rejected token means linking again. A 403 can't be a missing scope, which CanStream rules
    // out first, so it is RomM's streaming grant the user doesn't have.
    private static RommStreamException AccessFailure(RommRequestException e) =>
        e.StatusCode == 401 ? new RommStreamException(403, "needsRelink") : new RommStreamException(403, "unavailable");

    private static (string? Message, string? RomName) Detail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body!);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("detail", out var detail))
            {
                return detail.ValueKind == JsonValueKind.Object
                    ? (RommParser.Text(detail, "message"), RommParser.Text(detail, "rom_name"))
                    : (detail.ValueKind == JsonValueKind.String ? detail.GetString() : null, null);
            }
        }
        catch (JsonException)
        {
        }

        return (null, null);
    }

    private async Task<RommStreamingPlatform?> PlatformRowAsync(string token, RommPlatform platform, CancellationToken cancellationToken)
    {
        var settings = _source.Settings;
        if (settings == null || !await VersionOkAsync(settings, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var now = _clock();
        var key = settings.BaseUrl + "\n" + token;
        if (!_configs.TryGetValue(key, out var cached) || cached.ExpiresUtc <= now)
        {
            RommStreamingConfig? config = null;
            try
            {
                config = await new RommClient(_http(), settings.BaseUrl, token).GetStreamingConfigAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (RommAccounts.IsRommFailure(e) && !cancellationToken.IsCancellationRequested)
            {
                // A failure is kept as "no streaming" for the same minute, so RomM isn't asked on every detail.
            }

            cached = new Cached<RommStreamingConfig?>(config, now + ConfigLifetime);
            _configs[key] = cached;
        }

        return cached.Value is { Enabled: true } enabled && enabled.Platforms.TryGetValue(platform.Slug, out var row) ? row : null;
    }

    private async Task<bool> VersionOkAsync(RommSettings settings, CancellationToken cancellationToken)
    {
        var now = _clock();
        var cached = _versionOk;
        if (cached != null && cached.ExpiresUtc > now && cached.Key == settings.BaseUrl)
        {
            return cached.Value;
        }

        string? version = null;
        try
        {
            version = await new RommClient(_http(), settings.BaseUrl, string.Empty).GetVersionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (RommAccounts.IsRommFailure(e) && !cancellationToken.IsCancellationRequested)
        {
        }

        var ok = IsSupportedVersion(version);
        _versionOk = new Cached<bool>(ok, now + VersionLifetime, settings.BaseUrl);
        return ok;
    }

    private sealed class Cached<T>
    {
        public Cached(T value, DateTime expiresUtc, string? key = null)
        {
            Value = value;
            ExpiresUtc = expiresUtc;
            Key = key;
        }

        public T Value { get; }

        public DateTime ExpiresUtc { get; }

        public string? Key { get; }
    }
}
