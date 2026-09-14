using System.Net;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    private static readonly Uri PluginListUri = new("https://example.test/plugins.json");

    [Fact]
    public async Task RateLimitedListSkipsPerPluginRequests()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.hello", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.hello", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : throw new InvalidOperationException(
                "A per-plugin request should not follow a rate-limited list: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.True(viewModel.Plugins.IsRateLimited);
        Assert.Equal("GitHub is rate limiting; try later.", viewModel.Plugins.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OfflineWithNoCacheShowsAnError()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(_ =>
            throw new HttpRequestException("connection reset"));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.False(viewModel.Plugins.IsRateLimited);
        Assert.False(viewModel.Plugins.IsUsingCachedList);
        Assert.Equal("Could not reach the plugin list.", viewModel.Plugins.Error);
    }

    [Fact]
    public async Task CacheFallbackShowsTheListAge()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteCachedList(DateTimeOffset.UtcNow.AddHours(-3));
        var handler = new RoutedHandler(_ =>
            throw new HttpRequestException("connection reset"));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        Assert.True(viewModel.Plugins.IsUsingCachedList);
        Assert.Contains("3 hour(s)", viewModel.Plugins.ListAgeText, StringComparison.Ordinal);
        Assert.Null(viewModel.Plugins.Error);
    }

    [Fact]
    public async Task UpdateAndRemoveAreOfferedOnlyForLauncherManagedPlugins()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        fixture.WriteManifest("someone.manual", "1.0.0", ["headless"]);
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.True(managed.CanRemove);
        Assert.NotNull(managed.RemoveCommand);

        PluginInstalledRowViewModel manual = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "someone.manual");
        Assert.False(manual.CanRemove);
        Assert.Null(manual.RemoveCommand);
    }

    [Fact]
    public async Task UpdateWithheldReasonExplainsWhyNoUpdateIsOffered()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.managed", "0.1.0", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        Assert.False(managed.UpdateAvailable);
        Assert.True(managed.HasUpdateWithheldReason);
        Assert.Equal("not newer", managed.UpdateWithheldReason);
    }

    [Fact]
    public async Task RefreshDiscoverDetailsAsyncFillsInLatestVersionAndFetchesItOnce()
    {
        using var fixture = new PluginPanelFixture();
        byte[] remoteManifest = PluginPanelFixture.ManifestJson(
            "edwards.discoverable", "0.2.0", "0.1.0", ["headless", "graphical"]);
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri == PluginListUri)
            {
                return Ok(fixture.ListJson());
            }

            if (request.RequestUri == GitHubReleaseLocator.LatestAsset(
                "shaneedwards/openac-plugin-hello", "plugin.json"))
            {
                return Ok(remoteManifest);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        Assert.False(row.HasLatestVersion);

        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Equal("0.2.0", row.LatestVersion);
        Assert.Equal(LauncherPluginCompatibility.ClientNotInstalled, row.Compatibility);
        int detailRequests = handler.Requests.Count(uri => uri == GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json"));
        Assert.Equal(1, detailRequests);

        // A second Check pass and a second refresh must not re-fetch what the session already has.
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();
        await viewModel.Plugins.RefreshDiscoverDetailsAsync();

        Assert.Equal("0.2.0", Assert.Single(viewModel.Plugins.Discover).LatestVersion);
        Assert.Equal(1, handler.Requests.Count(uri => uri == GitHubReleaseLocator.LatestAsset(
            "shaneedwards/openac-plugin-hello", "plugin.json")));
    }

    [Fact]
    public async Task EscapeClosesTheInstallDialogWithoutInstalling()
    {
        using var fixture = new PluginPanelFixture();
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : throw new InvalidOperationException(
                "Escape must not trigger a network call: " + request.RequestUri));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginDiscoverRowViewModel row = Assert.Single(viewModel.Plugins.Discover);
        row.InstallCommand.Execute(null);
        Assert.True(viewModel.Plugins.InstallDialog.IsOpen);

        viewModel.CloseActiveModal();

        Assert.False(viewModel.Plugins.InstallDialog.IsOpen);
        Assert.False(viewModel.IsModalOpen);
        Assert.Equal(1, handler.Requests.Count(uri => uri == PluginListUri));
    }

    [Fact]
    public async Task EscapeClosesTheRemoveDialogWithoutRemoving()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.managed", "0.1.0", ["headless"]);
        fixture.AddRecord("edwards.managed", "shaneedwards/openac-plugin-hello", "0.1.0");
        var handler = new RoutedHandler(request => request.RequestUri == PluginListUri
            ? Ok(fixture.ListJson())
            : new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);
        await viewModel.Plugins.CheckNowCommand.ExecuteAsync();

        PluginInstalledRowViewModel managed = Assert.Single(
            viewModel.Plugins.Installed, row => row.Id == "edwards.managed");
        managed.RemoveCommand!.Execute(null);
        Assert.True(viewModel.Plugins.IsRemoveDialogOpen);

        viewModel.CloseActiveModal();

        Assert.False(viewModel.Plugins.IsRemoveDialogOpen);
        Assert.False(viewModel.IsModalOpen);
        Assert.True(Directory.Exists(Path.Combine(fixture.Paths.PluginsDirectory, "edwards.managed")));
    }

    [Fact]
    public void EnableForCharactersSkipsCharactersTheInstalledHostsDoNotSupport()
    {
        using var fixture = new PluginPanelFixture();
        fixture.WriteManifest("edwards.headless-only", "0.1.0", ["headless"]);
        var handler = new RoutedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using LauncherPluginComposition composition = LauncherPluginComposition.CreateForTest(
            fixture.Paths, PluginListUri, handler);
        using var orchestrator = new FakeLauncherOrchestrator
        {
            ServersOverride =
            [
                new LauncherServerSnapshot("Local ACE", "127.0.0.1", 9000,
                [
                    new LauncherAccountSnapshot("Local ACE", "testaccount",
                    [
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Graphical", "0x50000001",
                            LaunchMode.Gui, [], [], false, "Ready"),
                        new LauncherCharacterSnapshot(
                            "Local ACE", "testaccount", "+Headless", "0x50000002",
                            LaunchMode.Headless, [], [], false, "Ready"),
                    ],
                    HasRunningActivity: false,
                    ActivityStatus: "Ready"),
                ]),
            ],
        };
        using var viewModel = CreateInitialized(orchestrator);
        viewModel.ConfigurePlugins(composition, () => null);

        IReadOnlyList<PluginCharacterOption> all =
        [
            new("Local ACE", "testaccount", "+Graphical", "+Graphical (testaccount@Local ACE)"),
            new("Local ACE", "testaccount", "+Headless", "+Headless (testaccount@Local ACE)"),
        ];
        viewModel.Plugins.EnableForCharacters("edwards.headless-only", all);

        var update = Assert.Single(orchestrator.SettingsUpdates);
        Assert.Equal("+Headless", update.Character);
        Assert.Contains("edwards.headless-only", update.Plugins);
        Assert.Contains("+Graphical", viewModel.Plugins.Error);
        Assert.Contains("does not support that launch mode", viewModel.Plugins.Error);
    }

    private sealed class PluginPanelFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "acdream-plugins-panel-tests",
            Guid.NewGuid().ToString("N"));

        public PluginPanelFixture()
        {
            Paths = new ApplicationPathSet(
                Path.Combine(_root, "config"),
                Path.Combine(_root, "data"),
                Path.Combine(_root, "cache"),
                null);
        }

        public ApplicationPathSet Paths { get; }

        public void WriteManifest(string id, string version, IReadOnlyList<string> hosts)
        {
            string directory = Path.Combine(Paths.PluginsDirectory, id);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(
                Path.Combine(directory, "plugin.json"),
                ManifestJson(id, version, "0.1.0", hosts));
        }

        public static byte[] ManifestJson(
            string id, string version, string minHostVersion, IReadOnlyList<string> hosts)
        {
            string hostsJson = string.Join(", ", hosts.Select(host => $"\"{host}\""));
            return System.Text.Encoding.UTF8.GetBytes($$"""
                {
                  "id": "{{id}}",
                  "displayName": "{{id}}",
                  "version": "{{version}}",
                  "entryDll": "{{id}}.dll",
                  "apiVersion": 1,
                  "minHostVersion": "{{minHostVersion}}",
                  "hosts": [{{hostsJson}}]
                }
                """);
        }

        public void AddRecord(string id, string repo, string version)
        {
            InstalledPluginRecordStore store = InstalledPluginRecordStore.ForApplicationPaths(Paths);
            store.Load();
            store.Records.Add(new InstalledPluginRecord(
                id,
                repo,
                PluginInstallSource.Listed,
                version,
                "v" + version,
                new string('a', 64),
                DateTimeOffset.UtcNow,
                null,
                null));
            store.Save();
        }

        public byte[] ListJson(string discoverId = "edwards.discoverable") =>
            System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "{{discoverId}}", "name": "{{discoverId}}", "author": "Shane Edwards",
                  "description": "Test fixture.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": []
            }
            """);

        public void WriteCachedList(DateTimeOffset writeTime)
        {
            Directory.CreateDirectory(Paths.CacheDirectory);
            string path = Path.Combine(Paths.CacheDirectory, "plugins.json");
            File.WriteAllBytes(path, ListJson());
            File.SetLastWriteTimeUtc(path, writeTime.UtcDateTime);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private sealed class RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
