using System.Net;
using System.Text;
using System.Text.Json;
using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

/// <summary>
/// Runs streaming against a small fake of RomM's streaming API (5.3+): config, claim, status,
/// heartbeat and save-and-exit, always with the user's own token.
/// </summary>
public sealed class RommStreamingTests : IDisposable
{
    private const string BaseUrl = "http://romm.test:8080";
    private const string LibraryId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly RommPlatform Psx = new() { Id = 7, Slug = "psx", SystemId = "PlayStation", Core = "psx" };
    private static readonly RommPlatform Snes = new() { Id = 8, Slug = "snes", SystemId = "SNES", Core = "snes" };
    private static readonly RommGame Game = new() { Id = 42, PlatformId = 7, Title = "Vagrant Story" };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"romm-streaming-{Guid.NewGuid():N}");
    private readonly FakeRomm _romm = new();
    private DateTime _now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public async Task OffersAStreamForAPlatformRommStreams()
    {
        Link();
        var streaming = Streaming();

        var offer = await streaming.OfferAsync(User, Psx, CancellationToken.None);

        Assert.Equal("DuckStation", offer!.Label);
        Assert.False(offer.NeedsRelink);
        Assert.Null(await streaming.OfferAsync(User, Snes, CancellationToken.None));
        Assert.Equal("Bearer rmm_user", _romm.ConfigAuthorization);
    }

    [Fact]
    public async Task AsksRommForTheConfigOncePerMinute()
    {
        Link();
        var streaming = Streaming();

        await streaming.OfferAsync(User, Psx, CancellationToken.None);
        await streaming.OfferAsync(User, Snes, CancellationToken.None);
        Assert.Equal(1, _romm.ConfigCalls);

        _now = _now.AddSeconds(61);
        await streaming.OfferAsync(User, Psx, CancellationToken.None);
        Assert.Equal(2, _romm.ConfigCalls);
    }

    [Fact]
    public async Task OffersNothingToAnUnlinkedUserEvenWithTheAdminToken()
    {
        var offer = await Streaming().OfferAsync(User, Psx, CancellationToken.None);

        Assert.Null(offer);
        Assert.Equal(0, _romm.ConfigCalls);
        await Assert.ThrowsAsync<RommStreamException>(() => Streaming().StartAsync(User, Game, Psx, CancellationToken.None));
        Assert.Equal(0, _romm.Claims);
    }

    [Fact]
    public async Task AsksALinkFromBeforeStreamingToLinkAgain()
    {
        Link(withScopes: false);
        var streaming = Streaming();

        var offer = await streaming.OfferAsync(User, Psx, CancellationToken.None);
        Assert.True(offer!.NeedsRelink);
        Assert.Null(offer.Label);

        var refused = await Assert.ThrowsAsync<RommStreamException>(() => streaming.StartAsync(User, Game, Psx, CancellationToken.None));
        Assert.Equal("needsRelink", refused.Body.Error);
        Assert.Equal(0, _romm.Claims);
    }

    [Theory]
    [InlineData("5.2.3")]
    [InlineData(null)]
    public async Task OffersNothingOnARommTooOldForTheStreamingApi(string? version)
    {
        Link();
        _romm.Version = version;

        Assert.Null(await Streaming().OfferAsync(User, Psx, CancellationToken.None));
        Assert.Equal(0, _romm.ConfigCalls);
    }

    [Fact]
    public async Task OffersNothingWhenRommHasStreamingOffForTheUser()
    {
        Link();
        _romm.Enabled = false;

        Assert.Null(await Streaming().OfferAsync(User, Psx, CancellationToken.None));
    }

    [Fact]
    public async Task StartsPollsAndEndsASession()
    {
        Link();
        var streaming = Streaming();

        var session = await streaming.StartAsync(User, Game, Psx, CancellationToken.None);
        Assert.Equal("Station 1", session.Label);
        Assert.Equal("""{"rom_id":42}""", _romm.ClaimBody);

        _romm.Phase = "unpacking";
        var starting = await streaming.StatusAsync(User, Psx, session.SessionId, CancellationToken.None);
        Assert.Equal("starting", starting.State);
        Assert.Equal("unpacking", starting.Phase);
        Assert.Null(starting.RoomUrl);
        Assert.Contains("claimed_at=2026-11-19T12%3A00%3A00Z", _romm.LastQuery);

        _romm.Host = "https://play.example/s1/streaming/?token=abc";
        var ready = await streaming.StatusAsync(User, Psx, session.SessionId, CancellationToken.None);
        Assert.Equal("ready", ready.State);
        Assert.Equal("https://play.example/s1/streaming/?token=abc", ready.RoomUrl);

        var beat = await streaming.HeartbeatAsync(User, Psx, session.SessionId, CancellationToken.None);
        Assert.Equal(1, _romm.Heartbeats);
        Assert.Contains("container=webstation-s1", _romm.LastQuery);
        Assert.Equal("Bearer rmm_user", _romm.LastAuthorization);
        Assert.Equal("starting", beat.State); // RomM's heartbeat never repeats the room.

        await streaming.ExitAsync(User, Psx, session.SessionId, CancellationToken.None);
        Assert.Equal("""{"wait":false}""", _romm.ExitBody);

        _romm.Active = false;
        Assert.Equal("ended", (await streaming.StatusAsync(User, Psx, session.SessionId, CancellationToken.None)).State);
    }

    [Fact]
    public async Task ResolvesARoomOnRommsOwnOrigin()
    {
        Link();
        var streaming = Streaming();
        var session = await streaming.StartAsync(User, Game, Psx, CancellationToken.None);

        _romm.Host = "/s1/streaming/?token=abc";
        var ready = await streaming.StatusAsync(User, Psx, session.SessionId, CancellationToken.None);

        Assert.Equal(BaseUrl + "/s1/streaming/?token=abc", ready.RoomUrl);
    }

    [Theory]
    [InlineData("""{ "detail": { "message": "You already have a session on this platform", "draining": false, "rom_name": "Vagrant Story", "claimed_at": null } }""", 409, "alreadyPlaying", "Vagrant Story")]
    [InlineData("""{ "detail": { "message": "All 2 containers for this platform are in use", "draining": false, "rom_name": "Crash Bandicoot", "claimed_at": null } }""", 409, "busy", "Crash Bandicoot")]
    [InlineData("""{ "detail": { "code": "memory_card_import_required" } }""", 428, "needsCardImport", null)]
    [InlineData("""{ "detail": "Forbidden" }""", 403, "unavailable", null)]
    [InlineData("""{ "detail": "No streaming container configured" }""", 404, "unavailable", null)]
    public async Task ExplainsWhyAClaimFailed(string body, int status, string error, string? romName)
    {
        Link();
        _romm.ClaimFailure = (status, body);

        var refused = await Assert.ThrowsAsync<RommStreamException>(() => Streaming().StartAsync(User, Game, Psx, CancellationToken.None));

        Assert.Equal(error, refused.Body.Error);
        Assert.Equal(romName, refused.Body.RomName);
    }

    [Fact]
    public async Task RefusesASessionIdFromAnotherPlatform()
    {
        Link();
        var streaming = Streaming();
        var session = await streaming.StartAsync(User, Game, Psx, CancellationToken.None);

        var refused = await Assert.ThrowsAsync<RommStreamException>(() => streaming.HeartbeatAsync(User, Snes, session.SessionId, CancellationToken.None));
        Assert.Equal(400, refused.StatusCode);
        await Assert.ThrowsAsync<RommStreamException>(() => streaming.StatusAsync(User, Psx, "not-a-session", CancellationToken.None));
        Assert.Equal(0, _romm.Heartbeats);
    }

    [Fact]
    public async Task TreatsASessionRommAlreadyEndedAsEnded()
    {
        Link();
        var streaming = Streaming();
        var session = await streaming.StartAsync(User, Game, Psx, CancellationToken.None);
        _romm.SessionsGone = true;

        await streaming.ExitAsync(User, Psx, session.SessionId, CancellationToken.None);
        Assert.Equal("ended", (await streaming.HeartbeatAsync(User, Psx, session.SessionId, CancellationToken.None)).State);
    }

    [Theory]
    [InlineData("5.3.0", true)]
    [InlineData("5.4.1", true)]
    [InlineData("5.5.0-alpha.2", true)]
    [InlineData("v6.0", true)]
    [InlineData("development", true)]
    [InlineData("5.2.9", false)]
    [InlineData("4.9.0", false)]
    [InlineData("", false)]
    public void HoldsRommToTheFirstVersionWithTheStreamingApi(string version, bool supported) =>
        Assert.Equal(supported, RommStreaming.IsSupportedVersion(version));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://play.example/s1")]
    [InlineData("https://user:pass@play.example/s1")]
    [InlineData("")]
    public void RefusesARoomThatIsNotAWebPage(string host) =>
        Assert.Null(RommStreaming.RoomUrl(BaseUrl, host));

    [Fact]
    public void ReadsTheConfigLabelThenRommsEmulatorLabels()
    {
        using var document = JsonDocument.Parse("""
            { "enabled": true,
              "emulator_labels": { "pcsx2": "PCSX2" },
              "containers": [
                { "platform": "psx", "emulator": "duckstation", "label": "DuckStation", "host": "/s1" },
                { "platform": "ps2", "emulator": "pcsx2", "host": "/s2" },
                { "platform": "psx", "emulator": "swanstation", "label": "Second pool", "host": "/s3" },
                { "platform": "ngc", "host": "/s4" } ] }
            """);

        var config = RommParser.ParseStreamingConfig(document.RootElement);

        Assert.True(config.Enabled);
        Assert.Equal(2, config.Platforms.Count);
        Assert.Equal("DuckStation", config.Platforms["PSX"].Label);
        Assert.Equal("PCSX2", config.Platforms["ps2"].Label);
    }

    // Writes the user's stored link the way RommAccounts keeps it. A link made before streaming
    // has no scopes recorded.
    private void Link(bool withScopes = true)
    {
        Directory.CreateDirectory(_folder);
        var account = new Dictionary<string, object?>
        {
            ["baseUrl"] = BaseUrl,
            ["token"] = "rmm_user",
            ["username"] = "test",
        };
        if (withScopes)
        {
            account["scopes"] = new[] { "roms.read", "roms.user.write" };
        }

        File.WriteAllText(Path.Combine(_folder, User.ToString("N") + ".json"), JsonSerializer.Serialize(account));
    }

    private RommStreaming Streaming()
    {
        var source = new RommSource(
            () => RommSettings.From(BaseUrl, "rmm_admin", LibraryId),
            () => new HttpClient(_romm),
            RommTestRules.Rules,
            accountsFolder: _folder);
        return new RommStreaming(source, () => new HttpClient(_romm), () => _now);
    }

    private sealed class FakeRomm : HttpMessageHandler
    {
        public string? Version { get; set; } = "5.4.1";

        public bool Enabled { get; set; } = true;

        public bool Active { get; set; } = true;

        public bool SessionsGone { get; set; }

        public string? Host { get; set; }

        public string? Phase { get; set; }

        public (int Status, string Body)? ClaimFailure { get; set; }

        public int ConfigCalls { get; private set; }

        public int Claims { get; private set; }

        public int Heartbeats { get; private set; }

        public string? ConfigAuthorization { get; private set; }

        public string? LastAuthorization { get; private set; }

        public string LastQuery { get; private set; } = string.Empty;

        public string? ClaimBody { get; private set; }

        public string? ExitBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            LastQuery = request.RequestUri.Query;
            LastAuthorization = request.Headers.Authorization?.ToString();
            switch (path)
            {
                case "/api/heartbeat":
                    return Json(Version == null ? "{}" : $$"""{ "SYSTEM": { "VERSION": "{{Version}}" } }""");
                case "/api/streaming/config":
                    ConfigCalls++;
                    ConfigAuthorization = LastAuthorization;
                    return Json($$"""{ "enabled": {{(Enabled ? "true" : "false")}}, "emulator_labels": {}, "containers": [ { "platform": "psx", "emulator": "duckstation", "label": "DuckStation", "host": "https://play.example/s1" } ] }""");
                case "/api/streaming/sessions":
                    Claims++;
                    ClaimBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return ClaimFailure is { } failure
                        ? Json(failure.Body, (HttpStatusCode)failure.Status)
                        : Json("""{ "platform": "psx", "container": "webstation-s1", "label": "Station 1", "rom_name": "Vagrant Story", "claimed_at": "2026-11-19T12:00:00Z" }""", HttpStatusCode.Accepted);
            }

            if (SessionsGone && path.StartsWith("/api/streaming/sessions/", StringComparison.Ordinal))
            {
                return Json("""{ "detail": "No session" }""", HttpStatusCode.NotFound);
            }

            switch (path)
            {
                case "/api/streaming/sessions/psx/status":
                    return Json(Status(Host));
                case "/api/streaming/sessions/psx/heartbeat":
                    Heartbeats++;
                    return Json(Status(null));
                case "/api/streaming/sessions/psx/save-and-exit":
                    ExitBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                    return Json("""{ "status": "ok", "saved": true, "platform": "psx", "released": true }""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private string Status(string? host) => Active
            ? JsonSerializer.Serialize(new { status = "active", platform = "psx", host, extraction_phase = Phase })
            : """{ "status": "ended", "platform": "psx", "termination": null }""";

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
