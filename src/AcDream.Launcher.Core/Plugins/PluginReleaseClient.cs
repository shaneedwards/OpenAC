using System.Net;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Plugins;

public enum PluginReleaseFetchStatus
{
    Success,
    RateLimited,
    Unavailable,
}

/// <summary>A fetched small document (the list, a <c>plugin.json</c>, or a <c>.sha256</c>) and, when
/// the request followed a <c>latest/download</c> redirect, the release tag it resolved to.</summary>
public sealed record PluginReleaseDocument(byte[] Content, string? Tag);

public sealed record PluginReleaseFetchResult(
    PluginReleaseFetchStatus Status,
    PluginReleaseDocument? Document)
{
    public static PluginReleaseFetchResult Success(byte[] content, string? tag) =>
        new(PluginReleaseFetchStatus.Success, new PluginReleaseDocument(content, tag));

    public static readonly PluginReleaseFetchResult RateLimited =
        new(PluginReleaseFetchStatus.RateLimited, null);

    public static readonly PluginReleaseFetchResult Unavailable =
        new(PluginReleaseFetchStatus.Unavailable, null);
}

/// <summary>Fetches the small documents the plugin pipeline reads over HTTPS: the plugin list, a
/// release's <c>plugin.json</c>, and its <c>.sha256</c>. Reuses
/// <see cref="VerifiedArtifactDownloader.SendWithValidatedRedirectsAsync"/> for redirect handling and
/// <see cref="ReleaseManifestClient.RequireTransport"/> for transport checks (L-307).</summary>
public sealed class PluginReleaseClient
{
    public const int MaximumDocumentBytes = 256 * 1024;

    private readonly HttpClient _httpClient;

    public PluginReleaseClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    internal static PluginReleaseClient CreateForTransportTest(HttpMessageHandler handler) =>
        new(new HttpClient(handler));

    public async Task<PluginReleaseFetchResult> FetchDocumentAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ReleaseManifestClient.RequireSecureOrLoopback(url, "plugin release");

        string? tag = null;
        using HttpResponseMessage response = await VerifiedArtifactDownloader
            .SendWithValidatedRedirectsAsync(
                _httpClient,
                url,
                "plugin release",
                next => tag ??= ExtractTag(next),
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            return PluginReleaseFetchResult.RateLimited;
        if (response.StatusCode != HttpStatusCode.OK)
            return PluginReleaseFetchResult.Unavailable;

        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > MaximumDocumentBytes)
        {
            throw new LauncherUpdateException(
                $"The plugin release document is larger than {MaximumDocumentBytes} bytes.");
        }

        await using Stream input = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            if (output.Length + read > MaximumDocumentBytes)
            {
                throw new LauncherUpdateException(
                    $"The plugin release document is larger than {MaximumDocumentBytes} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return PluginReleaseFetchResult.Success(output.ToArray(), tag);
    }

    /// <summary>Reads the tag out of a redirect target shaped
    /// <c>/{owner}/{repo}/releases/download/{tag}/{asset}</c>; any other shape yields no tag.</summary>
    private static string? ExtractTag(Uri redirectTarget)
    {
        string[] segments = redirectTarget.AbsolutePath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index] == "releases"
                && index + 1 < segments.Length
                && segments[index + 1] == "download"
                && index + 2 < segments.Length)
            {
                return segments[index + 2];
            }
        }

        return null;
    }
}
