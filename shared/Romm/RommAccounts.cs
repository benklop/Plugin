using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.Romm;

/// <summary>What the apps show on their RomM account screen.</summary>
public sealed class RommAccountStatus
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("linked")]
    public bool Linked { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("pending")]
    public RommPendingLink? Pending { get; set; }

    /// <summary>expired, denied or unreachable, set on the answer right after it happened.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public sealed class RommPendingLink
{
    [JsonPropertyName("userCode")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verificationUrl")]
    public string VerificationUrl { get; set; } = string.Empty;

    [JsonPropertyName("verificationUrlComplete")]
    public string VerificationUrlComplete { get; set; } = string.Empty;

    [JsonPropertyName("expiresInSeconds")]
    public int ExpiresInSeconds { get; set; }

    [JsonPropertyName("intervalSeconds")]
    public int IntervalSeconds { get; set; }
}

/// <summary>
/// Links media server users to their own RomM accounts through RomM's device pairing. Each
/// user's token stays in the plugin's data folder and never reaches an app.
/// </summary>
public sealed class RommAccounts
{
    // Leaves out me.write, which would let a stored token change the user's RomM password. The
    // cost is that the plugin can't revoke its own token, so unlinking only forgets it.
    // roms.user.write starts, keeps alive and ends the user's streaming sessions.
    private static readonly string[] Scopes =
    {
        "roms.read", "platforms.read", "firmware.read", "assets.read", "assets.write", "me.read", "roms.user.write",
    };

    private const string StreamingScope = "roms.user.write";

    private readonly Func<RommSettings?> _settings;
    private readonly Func<HttpClient> _http;
    private readonly string _folder;
    private readonly ConcurrentDictionary<Guid, StoredAccount?> _accounts = new ConcurrentDictionary<Guid, StoredAccount?>();
    private readonly ConcurrentDictionary<Guid, PendingLink> _pending = new ConcurrentDictionary<Guid, PendingLink>();

    public RommAccounts(Func<RommSettings?> settings, Func<HttpClient> http, string folder)
    {
        _settings = settings;
        _http = http;
        _folder = folder;
    }

    /// <summary>The user's token for the RomM server configured now, or null.</summary>
    public string? TokenFor(Guid userId) => LinkedAccount(userId, _settings())?.Token;

    /// <summary>
    /// Whether the user's link asked RomM for the streaming scope. Links made before streaming
    /// recorded no scopes, so the user links again once to stream.
    /// </summary>
    public bool CanStream(Guid userId) =>
        LinkedAccount(userId, _settings())?.Scopes?.Contains(StreamingScope, StringComparer.Ordinal) == true;

    /// <summary>The user's link, after asking RomM whether a pending one was approved.</summary>
    public async Task<RommAccountStatus> StatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (settings == null)
        {
            return new RommAccountStatus();
        }

        var error = _pending.TryGetValue(userId, out var pending)
            ? await PollAsync(userId, settings, pending, cancellationToken).ConfigureAwait(false)
            : null;
        return Status(userId, settings, error);
    }

    public async Task<RommAccountStatus> StartLinkAsync(Guid userId, CancellationToken cancellationToken)
    {
        var settings = _settings();
        if (settings == null)
        {
            return new RommAccountStatus();
        }

        try
        {
            // The same identifier each time, so linking again reuses the user's RomM device.
            // shortcut: the approval link uses the address the plugin reaches RomM on, so a
            // Docker-internal hostname won't open on a phone. Add a public address setting if admins hit that.
            var link = await new RommClient(_http(), settings.BaseUrl, settings.Token)
                .StartDeviceLinkAsync("moonfin-" + userId.ToString("N"), "Moonfin", Scopes, cancellationToken)
                .ConfigureAwait(false);
            _pending[userId] = new PendingLink(link, settings.BaseUrl);
            return Status(userId, settings, null);
        }
        catch (Exception e) when (IsRommFailure(e))
        {
            return Status(userId, settings, "unreachable");
        }
    }

    public RommAccountStatus Unlink(Guid userId)
    {
        _pending.TryRemove(userId, out _);
        _accounts[userId] = null;
        var path = PathFor(userId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Status(userId, _settings(), null);
    }

    internal static bool IsRommFailure(Exception e) =>
        e is HttpRequestException || e is RommRequestException || e is TaskCanceledException || e is JsonException;

    private async Task<string?> PollAsync(Guid userId, RommSettings settings, PendingLink pending, CancellationToken cancellationToken)
    {
        if (pending.BaseUrl != settings.BaseUrl || DateTime.UtcNow >= pending.ExpiresUtc)
        {
            Forget(userId, pending);
            return "expired";
        }

        lock (pending)
        {
            // RomM answers slow_down to a poll inside the interval, so a busy app can't trip it.
            if (DateTime.UtcNow - pending.LastPollUtc < TimeSpan.FromSeconds(pending.Link.IntervalSeconds))
            {
                return null;
            }

            pending.LastPollUtc = DateTime.UtcNow;
        }

        string? token;
        string? reason;
        try
        {
            (token, reason) = await new RommClient(_http(), settings.BaseUrl, settings.Token)
                .PollDeviceLinkAsync(pending.Link.DeviceCode, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (IsRommFailure(e))
        {
            return "unreachable";
        }

        if (token != null)
        {
            string? username = null;
            try
            {
                username = await new RommClient(_http(), settings.BaseUrl, token).GetUsernameAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (IsRommFailure(e))
            {
                // The link works without a name, so a failed lookup only leaves it blank.
            }

            Save(userId, new StoredAccount { BaseUrl = settings.BaseUrl, Token = token, Username = username, Scopes = Scopes });
            Forget(userId, pending);
            return null;
        }

        if (reason == "authorization_pending" || reason == "slow_down")
        {
            return null;
        }

        Forget(userId, pending);
        return reason == "access_denied" ? "denied" : "expired";
    }

    private RommAccountStatus Status(Guid userId, RommSettings? settings, string? error)
    {
        if (settings == null)
        {
            return new RommAccountStatus();
        }

        var account = LinkedAccount(userId, settings);
        RommPendingLink? pendingView = null;
        if (_pending.TryGetValue(userId, out var pending))
        {
            pendingView = new RommPendingLink
            {
                UserCode = pending.Link.UserCode,
                VerificationUrl = pending.Link.VerificationUrl,
                VerificationUrlComplete = pending.Link.VerificationUrlComplete,
                ExpiresInSeconds = Math.Max(0, (int)(pending.ExpiresUtc - DateTime.UtcNow).TotalSeconds),
                IntervalSeconds = pending.Link.IntervalSeconds,
            };
        }

        return new RommAccountStatus
        {
            Available = true,
            Linked = account != null,
            Username = account?.Username,
            Pending = pendingView,
            Error = error,
        };
    }

    // Only drops the pairing that finished, not a newer one the user started meanwhile.
    private void Forget(Guid userId, PendingLink pending) =>
        ((ICollection<KeyValuePair<Guid, PendingLink>>)_pending).Remove(new KeyValuePair<Guid, PendingLink>(userId, pending));

    // A token from a RomM server the admin has since replaced doesn't count.
    private StoredAccount? LinkedAccount(Guid userId, RommSettings? settings)
    {
        var account = settings == null ? null : Load(userId);
        return account != null && account.BaseUrl == settings!.BaseUrl ? account : null;
    }

    private string PathFor(Guid userId) => Path.Combine(_folder, userId.ToString("N") + ".json");

    private StoredAccount? Load(Guid userId) => _accounts.GetOrAdd(userId, id =>
    {
        try
        {
            var path = PathFor(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<StoredAccount>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
        {
            return null;
        }
    });

    private void Save(Guid userId, StoredAccount account)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(PathFor(userId), JsonSerializer.Serialize(account));
        _accounts[userId] = account;
    }

    private sealed class StoredAccount
    {
        [JsonPropertyName("baseUrl")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        /// <summary>The scopes the link asked for. Null on links saved before this was kept.</summary>
        [JsonPropertyName("scopes")]
        public string[]? Scopes { get; set; }
    }

    private sealed class PendingLink
    {
        public PendingLink(RommDeviceLink link, string baseUrl)
        {
            Link = link;
            BaseUrl = baseUrl;
            ExpiresUtc = DateTime.UtcNow.AddSeconds(link.ExpiresInSeconds);
        }

        public RommDeviceLink Link { get; }

        public string BaseUrl { get; }

        public DateTime ExpiresUtc { get; }

        public DateTime LastPollUtc { get; set; } = DateTime.MinValue;
    }
}
