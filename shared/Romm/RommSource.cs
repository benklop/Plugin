using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>The RomM connection an admin configured, already checked for usable values.</summary>
public sealed class RommSettings
{
    private RommSettings(string baseUrl, string token, string libraryId)
    {
        BaseUrl = baseUrl;
        Token = token;
        LibraryId = libraryId;
    }

    public string BaseUrl { get; }

    public string Token { get; }

    /// <summary>The media server library whose games come from RomM.</summary>
    public string LibraryId { get; }

    /// <summary>Null unless the address, token and library are all set and usable.</summary>
    public static RommSettings? From(string? url, string? token, string? libraryId)
    {
        var baseUrl = RommClient.NormalizeBaseUrl(url);
        var normalizedToken = RommClient.NormalizeToken(token);
        var library = libraryId?.Trim();
        return baseUrl == null || normalizedToken == null || string.IsNullOrEmpty(library)
            ? null
            : new RommSettings(baseUrl, normalizedToken, library!);
    }

    internal string Signature => BaseUrl + "\n" + Token;

    internal RommSettings WithToken(string token) => new RommSettings(BaseUrl, token, LibraryId);
}

/// <summary>
/// One RomM-backed game library: which library it is, its inventory, and the ids Moonfin
/// gives its games and firmware. A changed address or token starts a fresh inventory.
/// </summary>
public sealed class RommSource
{
    public const string GameIdPrefix = "romm-";
    public const string FirmwareIdPrefix = "romm-fw-";

    private readonly Func<RommSettings?> _settings;
    private readonly Func<HttpClient> _httpClient;
    private readonly RommFileRules _rules;
    private readonly Func<RommSettings, CancellationToken, Task<RommSnapshot>>? _loadOverride;
    private readonly object _gate = new object();
    private readonly ConcurrentDictionary<Guid, RommSource> _userSources = new ConcurrentDictionary<Guid, RommSource>();
    private string? _inventorySignature;
    private RommInventory? _inventory;

    /// <param name="settings">Read on every call, so a saved change applies right away.</param>
    /// <param name="httpClient">Hands out the HttpClient each RomM call uses.</param>
    /// <param name="rules">The plugin's ROM file rules.</param>
    /// <param name="loadOverride">Replaces the RomM listing, for tests.</param>
    /// <param name="accountsFolder">Where users' own RomM links are kept. Without it everyone uses the admin's token.</param>
    public RommSource(
        Func<RommSettings?> settings,
        Func<HttpClient> httpClient,
        RommFileRules rules,
        Func<RommSettings, CancellationToken, Task<RommSnapshot>>? loadOverride = null,
        string? accountsFolder = null)
    {
        _settings = settings;
        _httpClient = httpClient;
        _rules = rules;
        _loadOverride = loadOverride;
        Accounts = accountsFolder == null ? null : new RommAccounts(settings, httpClient, accountsFolder);
        Streaming = Accounts == null ? null : new RommStreaming(this, httpClient);
    }

    public RommAccounts? Accounts { get; }

    /// <summary>Streaming from RomM's emulator containers. Needs users' own RomM links, so null without them.</summary>
    public RommStreaming? Streaming { get; }

    /// <summary>Raised when a RomM load changed the games clients see.</summary>
    public event Action? Changed;

    public RommSettings? Settings => _settings();

    public bool Owns(string? libraryId)
    {
        var settings = Settings;
        return settings != null && SameId(settings.LibraryId, libraryId);
    }

    /// <summary>The current snapshot, waiting briefly when none has loaded yet.</summary>
    public RommSnapshot? Snapshot() => CurrentInventory()?.Get();

    /// <summary>
    /// True once a snapshot has loaded. Never waits, so background work can skip the library
    /// until RomM has answered at least once.
    /// </summary>
    public bool IsReady()
    {
        var inventory = CurrentInventory();
        if (inventory == null)
        {
            return false;
        }

        if (inventory.Current != null)
        {
            return true;
        }

        inventory.StartRefresh();
        return false;
    }

    public string? LastError => CurrentInventory()?.LastError;

    /// <summary>What the admin debug route shows for RomM. The token is left out.</summary>
    public object? Diagnostics()
    {
        var settings = Settings;
        if (settings == null)
        {
            return null;
        }

        var snapshot = IsReady() ? Snapshot() : null;
        return new
        {
            url = settings.BaseUrl,
            libraryId = settings.LibraryId,
            loaded = snapshot != null,
            loadedUtc = snapshot?.LoadedUtc,
            platforms = snapshot?.Platforms.Count,
            games = snapshot?.Games.Count,
            lastError = LastError,
        };
    }

    /// <summary>
    /// The library as a linked user's own RomM account sees it, with that user's token for every
    /// call. Anyone not linked gets this source and the admin's token.
    /// </summary>
    public RommSource ForUser(Guid? userId)
    {
        if (userId == null || Accounts?.TokenFor(userId.Value) == null)
        {
            return this;
        }

        return _userSources.GetOrAdd(userId.Value, id => new RommSource(
            () =>
            {
                var settings = Settings;
                var token = Accounts.TokenFor(id);
                return settings == null || token == null ? null : settings.WithToken(token);
            },
            _httpClient,
            _rules,
            _loadOverride));
    }

    public RommClient? CreateClient()
    {
        var settings = Settings;
        return settings == null ? null : new RommClient(_httpClient(), settings.BaseUrl, settings.Token);
    }

    /// <summary>A client for values that may not be saved yet, for the admin page's test.</summary>
    public RommClient ClientFor(string baseUrl, string token) => new RommClient(_httpClient(), baseUrl, token);

    public string? AssetUrl(string? path)
    {
        var settings = Settings;
        return settings == null ? null : RommClient.AssetUrl(settings.BaseUrl, path);
    }

    public RommGame? FindGame(string? gameId) => FindGameAndPlatform(gameId)?.Game;

    public (RommGame Game, RommPlatform Platform)? FindGameAndPlatform(string? gameId)
    {
        var id = ParseId(gameId, GameIdPrefix);
        var snapshot = id == null ? null : Snapshot();
        var game = snapshot?.FindGame(id!.Value);
        var platform = game == null ? null : snapshot!.FindPlatform(game.PlatformId);
        return game == null || platform == null ? ((RommGame, RommPlatform)?)null : (game, platform);
    }

    public RommFile? FindFirmware(string? firmwareId)
    {
        var id = ParseId(firmwareId, FirmwareIdPrefix);
        var snapshot = id == null ? null : Snapshot();
        if (snapshot == null)
        {
            return null;
        }

        foreach (var platform in snapshot.Platforms)
        {
            foreach (var firmware in platform.Firmware)
            {
                if (firmware.Id == id)
                {
                    return firmware;
                }
            }
        }

        return null;
    }

    public static string GameId(RommGame game) => GameIdPrefix + game.Id.ToString(CultureInfo.InvariantCulture);

    public static string FirmwareId(RommFile firmware) => FirmwareIdPrefix + firmware.Id.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Compares library ids the way the media servers hand them out, where one API reports a
    /// dashed GUID and another the compact form.
    /// </summary>
    public static bool SameId(string? a, string? b)
    {
        var x = a?.Trim();
        var y = b?.Trim();
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
        {
            return false;
        }

        return Guid.TryParse(x, out var gx) && Guid.TryParse(y, out var gy)
            ? gx == gy
            : string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    }

    private static int? ParseId(string? value, string prefix)
    {
        // A firmware id also starts with the game prefix, so a game lookup must not take one.
        if (value == null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            (prefix == GameIdPrefix && value.StartsWith(FirmwareIdPrefix, StringComparison.Ordinal)))
        {
            return null;
        }

        return int.TryParse(value.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : (int?)null;
    }

    private RommInventory? CurrentInventory()
    {
        var settings = Settings;
        if (settings == null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_inventory == null || !string.Equals(_inventorySignature, settings.Signature, StringComparison.Ordinal))
            {
                var inventory = new RommInventory(cancellationToken => _loadOverride != null
                    ? _loadOverride(settings, cancellationToken)
                    : new RommClient(_httpClient(), settings.BaseUrl, settings.Token).LoadAsync(_rules, cancellationToken));
                inventory.Changed += () => Changed?.Invoke();
                _inventory = inventory;
                _inventorySignature = settings.Signature;
            }

            return _inventory;
        }
    }
}
