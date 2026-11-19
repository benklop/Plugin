using System.Net;
using System.Text;
using Moonfin.Romm;
using Xunit;

namespace Moonfin.Romm.Tests;

/// <summary>
/// Runs pairing and save sync against a small fake of RomM's device flow, saves and states.
/// </summary>
public sealed class RommAccountTests : IDisposable
{
    private const string BaseUrl = "http://romm.test:8080";
    private const string LibraryId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private static readonly Guid User = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"romm-accounts-{Guid.NewGuid():N}");
    private readonly FakeRomm _romm = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    [Fact]
    public async Task LinksAfterTheUserApprovesInRomm()
    {
        var source = Source();
        var accounts = source.Accounts!;

        var started = await accounts.StartLinkAsync(User, CancellationToken.None);
        Assert.True(started.Available);
        Assert.False(started.Linked);
        Assert.Equal("ABCD1234", started.Pending!.UserCode);
        Assert.Equal(BaseUrl + "/pair/device?code=ABCD1234", started.Pending.VerificationUrlComplete);
        Assert.Null(accounts.TokenFor(User));

        _romm.Approved = true;
        var linked = await accounts.StatusAsync(User, CancellationToken.None);

        Assert.True(linked.Linked);
        Assert.Equal("test", linked.Username);
        Assert.Null(linked.Pending);
        Assert.Equal("rmm_user", accounts.TokenFor(User));
        Assert.DoesNotContain("me.write", _romm.RequestedScopes);
        Assert.Equal("moonfin-" + User.ToString("N"), _romm.DeviceIdentifier);

        // The link survives a restart, and the token is the user's for every RomM call.
        var reloaded = Source();
        Assert.Equal("rmm_user", reloaded.Accounts!.TokenFor(User));
        reloaded.ForUser(User).Snapshot();
        Assert.Equal("Bearer rmm_user", _romm.IdsAuthorization);
        Assert.Same(reloaded, reloaded.ForUser(Guid.NewGuid()));
    }

    [Fact]
    public async Task ALinkedUserSeesTheSharedLibraryCutToWhatRommShowsThem()
    {
        var source = Source();
        await Link(source.Accounts!);
        _romm.UserSees = new[] { 12 };

        var everyone = source.Snapshot()!;
        var mine = source.ForUser(User).Snapshot()!;

        Assert.Equal(new[] { 12, 13 }, everyone.Games.Select(game => game.Id));
        Assert.Equal(12, Assert.Single(mine.Games).Id);
        Assert.Null(source.ForUser(User).FindGame("romm-13"));
        Assert.Equal(1, _romm.Listings);
        Assert.Equal("Bearer rmm_admin", _romm.LastListingAuthorization);
    }

    [Fact]
    public async Task PollsRommNoFasterThanItAsks()
    {
        var accounts = Source().Accounts!;
        await accounts.StartLinkAsync(User, CancellationToken.None);

        await accounts.StatusAsync(User, CancellationToken.None);
        await accounts.StatusAsync(User, CancellationToken.None);

        Assert.Equal(1, _romm.Polls);
    }

    [Fact]
    public async Task ReportsADeniedLinkOnce()
    {
        var accounts = Source().Accounts!;
        await accounts.StartLinkAsync(User, CancellationToken.None);
        _romm.Denied = true;

        var denied = await accounts.StatusAsync(User, CancellationToken.None);
        var after = await accounts.StatusAsync(User, CancellationToken.None);

        Assert.Equal("denied", denied.Error);
        Assert.Null(denied.Pending);
        Assert.Null(after.Error);
    }

    [Fact]
    public async Task CancelingBeforeAnyoneLinkedDropsTheCode()
    {
        var accounts = Source().Accounts!;
        await accounts.StartLinkAsync(User, CancellationToken.None);

        var status = accounts.Unlink(User);

        Assert.Null(status.Pending);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public async Task UnlinkingForgetsTheToken()
    {
        var accounts = Source().Accounts!;
        await Link(accounts);

        var status = accounts.Unlink(User);

        Assert.False(status.Linked);
        Assert.Null(accounts.TokenFor(User));
        Assert.Null(Source().Accounts!.TokenFor(User));
    }

    [Fact]
    public async Task ATokenForAnotherRommServerIsIgnored()
    {
        var url = BaseUrl;
        var source = Source(() => url);
        await Link(source.Accounts!);

        url = "http://other.test:8080";

        Assert.Null(source.Accounts!.TokenFor(User));
        Assert.False((await source.Accounts.StatusAsync(User, CancellationToken.None)).Linked);
    }

    [Fact]
    public async Task OnlyALinkedUsersRommSavesGoToRomm()
    {
        const string path = "/saves/a";
        var sync = new RommSaveSync(Source());
        Assert.Null(sync.Target(User, "ejs-snes-romm-12", "state", path));

        await Link(Source().Accounts!);
        sync = new RommSaveSync(Source());

        var state = sync.Target(User, "ejs-snes-romm-12", "state", path)!;
        Assert.True(state.IsState);
        Assert.Equal("ejs-snes", state.Emulator);
        Assert.Equal("moonfin-ejs-snes.state", state.FileName);
        Assert.Equal(path, state.LocalPath);

        var sram = sync.Target(User, "romm-12", "sram", path)!;
        Assert.False(sram.IsState);
        Assert.Equal("snes", sram.Emulator);
        Assert.Equal("Super Mario World (USA).srm", sram.FileName);

        Assert.Null(sync.Target(User, "romm-12", "state", path));
        Assert.Null(sync.Target(User, "romm-12", "settings", path));
        Assert.Null(sync.Target(User, "ejs-snes-abc123", "state", path));
        Assert.Null(sync.Target(User, "romm-99", "sram", path));
        Assert.Null(sync.Target(User, "romm-12", "sram", null));
    }

    [Fact]
    public async Task TheNewerCopyWinsAndRommGetsEveryWrite()
    {
        await Link(Source().Accounts!);
        var sync = new RommSaveSync(Source());
        var path = Path.Combine(_folder, "saves", "romm-12.sram");
        var target = sync.Target(User, "romm-12", "sram", path)!;

        Assert.True(await sync.WriteAsync(target, new byte[] { 1 }, CancellationToken.None));
        var uploaded = Assert.Single(_romm.Saves);
        Assert.Equal("snes", uploaded.Emulator);
        Assert.Equal(uploaded.UpdatedUtc, File.GetLastWriteTimeUtc(path));

        // Another device saved to RomM later, so its copy replaces the plugin's.
        _romm.Saves.Add(new FakeAsset(2, new byte[] { 2 }, "snes9x", uploaded.UpdatedUtc.AddMinutes(5)));
        Assert.Equal(new byte[] { 2 }, await sync.ReadAsync(target, CancellationToken.None));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(path));

        // The same bytes again make no new RomM version.
        Assert.True(await sync.WriteAsync(target, new byte[] { 2 }, CancellationToken.None));
        Assert.Equal(2, _romm.Saves.Count);
    }

    [Fact]
    public async Task KeepsTheSaveWhileRommIsDownAndSendsItLater()
    {
        await Link(Source().Accounts!);
        var sync = new RommSaveSync(Source());
        var target = sync.Target(User, "ejs-snes-romm-12", "state", Path.Combine(_folder, "saves", "ejs-snes-romm-12.state"))!;

        _romm.Down = true;
        Assert.False(await sync.WriteAsync(target, new byte[] { 7, 7 }, CancellationToken.None));
        Assert.Equal(new byte[] { 7, 7 }, await sync.ReadAsync(target, CancellationToken.None));

        _romm.Down = false;
        Assert.Equal(new byte[] { 7, 7 }, await sync.ReadAsync(target, CancellationToken.None));
        await WaitFor(() => _romm.States.Count == 1);
        Assert.Equal(new byte[] { 7, 7 }, _romm.States[0].Data);
        Assert.Equal("moonfin-ejs-snes.state", _romm.States[0].FileName);
    }

    private async Task Link(RommAccounts accounts)
    {
        await accounts.StartLinkAsync(User, CancellationToken.None);
        _romm.Approved = true;
        Assert.True((await accounts.StatusAsync(User, CancellationToken.None)).Linked);
    }

    private RommSource Source(Func<string>? url = null) => new(
        () => RommSettings.From(url?.Invoke() ?? BaseUrl, "rmm_admin", LibraryId),
        () => new HttpClient(_romm),
        RommTestRules.Rules,
        accountsFolder: _folder);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private sealed record FakeAsset(int Id, byte[] Data, string Emulator, DateTime UpdatedUtc, string FileName = "");

    private sealed class FakeRomm : HttpMessageHandler
    {
        private int _nextId = 100;

        public bool Approved { get; set; }

        public bool Denied { get; set; }

        public bool Down { get; set; }

        public int Polls { get; private set; }

        public string RequestedScopes { get; private set; } = string.Empty;

        public string? DeviceIdentifier { get; private set; }

        public string? LastListingAuthorization { get; private set; }

        public string? IdsAuthorization { get; private set; }

        public int Listings { get; private set; }

        /// <summary>The ROM ids RomM shows the linked user, the way its hidden ROMs and platforms leave them.</summary>
        public int[] UserSees { get; set; } = { 12, 13 };

        public List<FakeAsset> Saves { get; } = new();

        public List<FakeAsset> States { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Down)
            {
                throw new HttpRequestException("RomM is down.");
            }

            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            switch (path)
            {
                case "/api/auth/device/init":
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    RequestedScopes = body;
                    DeviceIdentifier = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("client_device_identifier").GetString();
                    return Json("""{ "device_code": "dev", "user_code": "ABCD1234", "verification_path": "/pair/device", "verification_path_complete": "/pair/device?code=ABCD1234", "expires_in": 600, "interval": 5 }""");
                case "/api/auth/device/token":
                    Polls++;
                    return Approved
                        ? Json("""{ "access_token": "rmm_user", "device_id": "d1", "scopes": [] }""")
                        : Json($$"""{ "detail": "{{(Denied ? "access_denied" : "authorization_pending")}}" }""", HttpStatusCode.BadRequest);
                case "/api/users/me":
                    return Json("""{ "username": "test" }""");
                case "/api/platforms":
                    LastListingAuthorization = request.Headers.Authorization?.ToString();
                    return Json("""[ { "id": 10, "slug": "snes", "fs_slug": "snes", "display_name": "SNES", "firmware": [] } ]""");
                case "/api/roms":
                    Listings++;
                    return Json("""
                        { "items": [
                          { "id": 12, "platform_id": 10, "name": "Super Mario World", "files": [ { "id": 15, "file_name": "Super Mario World (USA).sfc", "file_size_bytes": 9 } ] },
                          { "id": 13, "platform_id": 10, "name": "F-Zero", "files": [ { "id": 16, "file_name": "F-Zero (USA).sfc", "file_size_bytes": 9 } ] }
                        ] }
                        """);
                case "/api/roms/identifiers":
                    var auth = request.Headers.Authorization?.ToString();
                    IdsAuthorization = auth;
                    return Json(auth == "Bearer rmm_user" ? "[" + string.Join(",", UserSees) + "]" : "[12, 13]");
                case "/api/saves":
                    return request.Method == HttpMethod.Post
                        ? await Upload(request, Saves, query)
                        : Json(List(Saves));
                case "/api/states":
                    return request.Method == HttpMethod.Post
                        ? await Upload(request, States, query)
                        : Json(List(States));
            }

            var content = Saves.Concat(States).FirstOrDefault(asset => path.EndsWith($"/{asset.Id}/content", StringComparison.Ordinal));
            return content == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content.Data) };
        }

        private async Task<HttpResponseMessage> Upload(HttpRequestMessage request, List<FakeAsset> assets, string query)
        {
            var form = (MultipartFormDataContent)request.Content!;
            var file = form.Single();
            var emulator = Uri.UnescapeDataString(query.Split('&').First(part => part.Contains("emulator=")).Split('=')[1]);
            var asset = new FakeAsset(
                _nextId++,
                await file.ReadAsByteArrayAsync(),
                emulator,
                new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc).AddMinutes(_nextId),
                file.Headers.ContentDisposition!.FileName!.Trim('"'));
            // RomM replaces a state with the same name, while each slotted save is a new version.
            if (assets == States)
            {
                assets.RemoveAll(existing => existing.FileName == asset.FileName);
            }

            assets.Add(asset);
            return Json(Describe(asset));
        }

        private static string List(IEnumerable<FakeAsset> assets) => "[" + string.Join(",", assets.Select(Describe)) + "]";

        private static string Describe(FakeAsset asset) =>
            $$"""{ "id": {{asset.Id}}, "file_name": "{{asset.FileName}}", "emulator": "{{asset.Emulator}}", "updated_at": "{{asset.UpdatedUtc:yyyy-MM-ddTHH:mm:ss}}Z" }""";

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
