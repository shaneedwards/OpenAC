using System.Net;
using System.Text;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher;

/// <summary>One listed plugin not yet installed, as shown on the Discover tab.</summary>
internal sealed record PluginDiscoverEntry(
    string Id,
    string Name,
    string Author,
    string Description,
    string Repo);

/// <summary>The result of one Check pass (launcher start or Check now): the effective catalog,
/// whether GitHub throttled the request, the cached list's age when a fetch could not be made, and
/// the rows built from it.</summary>
internal sealed record PluginCheckOutcome(
    PluginCatalog? Catalog,
    bool RateLimited,
    DateTimeOffset? ListAgeUtc,
    IReadOnlyList<InstalledPluginInfo> Installed,
    IReadOnlyList<PluginDiscoverEntry> Discover,
    IReadOnlyList<string> UpdateAvailableIds);

/// <summary>Composition root for the launcher's plugin install feature (L-300, L-308, L-309),
/// beside <see cref="LauncherUpdateComposition"/>. Builds the release client, the installer, the
/// inventory and the record store; runs recovery once at start; and owns the Check pipeline, which
/// both the start-of-launcher check and "Check now" share, and which never blocks launching.</summary>
internal sealed class LauncherPluginComposition : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ApplicationPathSet _paths;
    private readonly string _cacheFilePath;

    private LauncherPluginComposition(
        ApplicationPathSet paths,
        Uri listUri,
        HttpClient httpClient,
        PluginReleaseClient releaseClient,
        InstalledPluginRecordStore recordStore,
        PluginInventory inventory,
        PluginInstaller installer)
    {
        _paths = paths;
        _httpClient = httpClient;
        ListUri = listUri;
        ReleaseClient = releaseClient;
        RecordStore = recordStore;
        Inventory = inventory;
        Installer = installer;
        _cacheFilePath = Path.Combine(paths.CacheDirectory, "plugins.json");
    }

    public Uri ListUri { get; }

    public PluginReleaseClient ReleaseClient { get; }

    public InstalledPluginRecordStore RecordStore { get; }

    public PluginInventory Inventory { get; }

    public PluginInstaller Installer { get; }

    /// <summary>The catalog from the most recent successful or cache-backed Check, for filtering
    /// blocked ids out of the next launched session (L-302).</summary>
    public PluginCatalog? CurrentCatalog { get; private set; }

    public static LauncherPluginComposition Create(ApplicationPathSet paths, Uri listUri)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(listUri);

        var httpClient = new HttpClient(
            new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
            },
            disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("OpenAC-launcher/1");

        var recordStore = InstalledPluginRecordStore.ForApplicationPaths(paths);
        try
        {
            recordStore.Load();
        }
        catch (LauncherUpdateException)
        {
            // A corrupted record only costs the launcher its memory of which plugins it manages
            // (Update/Remove fall back to unavailable, matching a manual install); it should never
            // keep the window from opening.
        }

        var inventory = new PluginInventory(paths, recordStore);
        var installer = new PluginInstaller(paths, httpClient, recordStore, inventory);
        var releaseClient = new PluginReleaseClient(httpClient);
        return new LauncherPluginComposition(
            paths, listUri, httpClient, releaseClient, recordStore, inventory, installer);
    }

    /// <summary>The launch-time Recovery pipeline (L-309), run once before the inventory is first
    /// read.</summary>
    public void Recover() => Installer.Recover();

    /// <summary>The Check pipeline: fetches the list, falls back to the last cached copy on
    /// failure, and writes a fresh cache on success so a later launch can filter blocked ids even
    /// with no network. Never throws; every failure is reported through the returned outcome.</summary>
    public async Task<PluginCheckOutcome> CheckAsync(
        ClientVersionResolution? clientResolution,
        CancellationToken cancellationToken = default)
    {
        bool rateLimited = false;
        DateTimeOffset? listAge = null;
        PluginCatalog? catalog;
        try
        {
            PluginReleaseFetchResult fetch = await ReleaseClient
                .FetchDocumentAsync(ListUri, cancellationToken)
                .ConfigureAwait(false);
            switch (fetch.Status)
            {
                case PluginReleaseFetchStatus.Success:
                    catalog = PluginCatalog.Parse(Encoding.UTF8.GetString(fetch.Document!.Content));
                    WriteCache(fetch.Document.Content);
                    break;
                case PluginReleaseFetchStatus.RateLimited:
                    rateLimited = true;
                    catalog = TryReadCache(out listAge);
                    break;
                default:
                    catalog = TryReadCache(out listAge);
                    break;
            }
        }
        catch (LauncherUpdateException)
        {
            catalog = TryReadCache(out listAge);
        }

        CurrentCatalog = catalog;

        IReadOnlyList<InstalledPluginInfo> installed = Inventory.Build(clientResolution, catalog);
        var updateAvailable = new List<string>();
        foreach (InstalledPluginInfo info in installed)
        {
            InstalledPluginRecord? record = RecordStore.Find(info.Id);
            if (record is null || record.Pending is not null)
            {
                continue;
            }

            if (await HasUpdateAsync(record, catalog, cancellationToken).ConfigureAwait(false))
            {
                updateAvailable.Add(info.Id);
            }
        }

        var installedIds = new HashSet<string>(
            installed.Select(info => info.Id),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<PluginDiscoverEntry> discover = catalog is null
            ? []
            : [.. catalog.Plugins
                .Where(entry => !installedIds.Contains(entry.Id))
                .Select(entry => new PluginDiscoverEntry(
                    entry.Id, entry.Name, entry.Author, entry.Description, entry.Repo))];

        return new PluginCheckOutcome(catalog, rateLimited, listAge, installed, discover, updateAvailable);
    }

    /// <summary>Newer-and-not-blocked only: an advisory badge, not an enforcement decision. The
    /// finer min/max/skip compatibility gate lives on <see cref="PluginInstaller"/>, which refuses
    /// the update itself when the user acts on it.</summary>
    private async Task<bool> HasUpdateAsync(
        InstalledPluginRecord record,
        PluginCatalog? catalog,
        CancellationToken cancellationToken)
    {
        if (record.Version is not { } currentVersionText
            || !LauncherVersion.TryParse(currentVersionText, out LauncherVersion? currentVersion))
        {
            return false;
        }

        PluginReleaseFetchResult fetch;
        try
        {
            fetch = await ReleaseClient
                .FetchDocumentAsync(
                    GitHubReleaseLocator.LatestAsset(record.Repo, "plugin.json"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LauncherUpdateException)
        {
            return false;
        }

        if (fetch.Status != PluginReleaseFetchStatus.Success)
        {
            return false;
        }

        LauncherPluginManifest manifest;
        try
        {
            manifest = LauncherPluginManifest.Parse(
                Encoding.UTF8.GetString(fetch.Document!.Content));
        }
        catch (LauncherPluginManifestException)
        {
            return false;
        }

        return LauncherVersion.TryParse(manifest.Version, out LauncherVersion? remoteVersion)
            && remoteVersion.CompareTo(currentVersion) > 0
            && catalog?.IsBlocked(record.Id, remoteVersion) != true;
    }

    private void WriteCache(byte[] content)
    {
        try
        {
            Directory.CreateDirectory(_paths.CacheDirectory);
            string tempPath = _cacheFilePath + ".tmp";
            File.WriteAllBytes(tempPath, content);
            File.Move(tempPath, _cacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private PluginCatalog? TryReadCache(out DateTimeOffset? ageUtc)
    {
        ageUtc = null;
        if (!File.Exists(_cacheFilePath))
        {
            return null;
        }

        try
        {
            ageUtc = File.GetLastWriteTimeUtc(_cacheFilePath);
            return PluginCatalog.Parse(File.ReadAllText(_cacheFilePath));
        }
        catch (Exception ex) when (ex is LauncherUpdateException or IOException)
        {
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
