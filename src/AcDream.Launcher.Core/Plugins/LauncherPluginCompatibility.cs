using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

/// <summary>Checks a launcher-read manifest's declared client versions and hosts against the
/// installed client. Reason strings match <c>AcDream.Core.Plugins.PluginHostCompatibility</c>
/// (L-303), except for the client-not-installed case, which that host never sees.</summary>
public static class LauncherPluginCompatibility
{
    public const string ClientNotInstalled = "client not installed";

    public static string? Evaluate(
        LauncherPluginManifest manifest,
        LaunchMode launchMode,
        LauncherVersion? clientVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        LauncherPluginHostKind host = ToHostKind(launchMode);
        IReadOnlyList<LauncherPluginHostKind> hosts = manifest.Hosts
            ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        if (!hosts.Contains(host))
        {
            return $"runs only on the "
                + $"{string.Join(" or ", hosts.Select(DescribeHost))} host";
        }

        LauncherPluginHostVersion? installed = LauncherPluginHostVersion.FromLauncherVersion(clientVersion);
        if (installed is not { } version)
            return ClientNotInstalled;

        if (manifest.MinHostVersion is { } min && version.CompareTo(min) < 0)
            return $"requires OpenAC {min} or newer (this is {version})";

        if (manifest.MaxHostVersion is { } max && version.CompareTo(max) > 0)
            return $"supports OpenAC up to {max} (this is {version})";

        if (manifest.SkipHostVersions.Contains(version))
            return $"is marked broken on OpenAC {version}";

        return null;
    }

    private static LauncherPluginHostKind ToHostKind(LaunchMode mode) => mode switch
    {
        LaunchMode.Headless => LauncherPluginHostKind.Headless,
        _ => LauncherPluginHostKind.Graphical,
    };

    private static string DescribeHost(LauncherPluginHostKind host) => host switch
    {
        LauncherPluginHostKind.Graphical => "graphical",
        LauncherPluginHostKind.Headless => "headless",
        _ => host.ToString(),
    };
}
