using System.Text.Json;

namespace Dashio.Core.Updates;

/// <summary>A published release of Dashio.</summary>
public sealed record Release(Version Version, string Tag, string Page);

/// <param name="Latest">The newest release there is, or null when none could be read.</param>
/// <param name="Error">Set when GitHub could not be asked.</param>
public sealed record UpdateResult(Release? Latest, bool IsNewer, string? Error = null);

/// <summary>
/// Asks GitHub which releases of Dashio exist. This is the only place Dashio uses the network,
/// and it only runs when the user presses "Check for updates". Nothing is downloaded: the
/// release page is opened in the browser.
/// </summary>
public sealed class UpdateChecker(HttpMessageHandler? handler = null)
{
    public const string Repository = "EverythingSuckz/Dashio";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases";

    private const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases?per_page=20";
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(15);

    public async Task<UpdateResult> CheckAsync(Version current, CancellationToken cancellation = default)
    {
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = GiveUpAfter;
            // GitHub refuses requests that do not say who is asking. Only the app's name is sent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Dashio");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            var latest = Newest(await client.GetStringAsync(ReleasesApi, cancellation));
            return new UpdateResult(latest, latest is not null && latest.Version > current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new UpdateResult(null, false, "GitHub could not be reached.");
        }
    }

    /// <summary>The release with the highest version in GitHub's answer. Drafts and tags that are not versions are passed over.</summary>
    public static Release? Newest(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        Release? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (!release.TryGetProperty("tag_name", out var tagName) || tagName.GetString() is not { } tag)
                continue;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
                continue;
            if (newest is null || version > newest.Version)
                newest = new Release(version, tag, $"{ReleasesPage}/tag/{Uri.EscapeDataString(tag)}");
        }
        return newest;
    }
}
