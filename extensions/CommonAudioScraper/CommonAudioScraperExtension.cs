using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public sealed class CommonAudioScraperExtension : IScraperProvider
{
    private const string ExtensionId = "cove.official.scrapers.common-audio";
    private const string AudioScraperId = "cove.official.scrapers.common-audio/scraper";
    private IServiceProvider? _services;

    private static readonly ScraperDescriptor AudioScraper = new(
        AudioScraperId,
        "Common Audio Sites",
        ScraperEntity.Audio,
        ScraperCapabilities.ByUrl,
        ["soundgasm.net/*", "*.soundgasm.net/*", "whyp.it/*", "*.whyp.it/*"],
        ScraperRiskLevel.NetworkOnly);

    public string Id => ExtensionId;
    public string Name => "Common Audio Scraper";
    public string Version => "1.0.0";
    public string? Description => "Extracts audio metadata from common hosted-audio pages such as Soundgasm and Whyp.";
    public string? Author => "Cove Team";
    public string? Url => OfficialDownloaderUtilities.RepoUrl;
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Scraper, ExtensionCategories.Metadata];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [AudioScraper];

    public async Task<ScrapedAudioDto?> ScrapeAudioAsync(ScraperRequest<AudioScrapeInput> request, CancellationToken ct)
    {
        var url = request.Input.Url ?? request.Input.Urls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url) || !IsSupportedUrl(url))
            return null;

        var info = await TryGetAudioInfoAsync(url, ct);
        if (info == null)
            return null;

        return new ScrapedAudioDto
        {
            Title = info.Title,
            Details = info.Details,
            Urls = [info.PageUrl],
            PerformerNames = string.IsNullOrWhiteSpace(info.Creator) ? [] : [info.Creator],
            TagNames = info.TagNames,
        };
    }

    private static bool IsSupportedUrl(string url)
    {
        return OfficialDownloaderUtilities.IsHttpUrl(url) && OfficialDownloaderUtilities.IsDirectAudioSite(url);
    }

    private async Task<AudioPageInfo?> TryGetAudioInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            return await GetAudioInfoAsync(url, ct);
        }
        catch
        {
            return null;
        }
    }

    private async Task<AudioPageInfo> GetAudioInfoAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CoveCommonAudioDownloader/1.0");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var fallbackTitle = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Audio");
        var rawTitle = CleanAudioTitle(ExtractSoundgasmTitle(html) ?? OfficialDownloaderUtilities.ExtractHtmlTitle(html), url);
        var audioUrl = ExtractAudioUrl(html, url)
            ?? throw new InvalidOperationException("The audio page was downloaded, but no audio file URL could be found.");
        var creator = ExtractAudioCreator(html, url);
        var details = ExtractAudioDescription(html);
        var metadata = OfficialDownloaderUtilities.ExtractBracketedMetadata(rawTitle, details, fallbackTitle);

        return new AudioPageInfo(url, metadata.Title, audioUrl, creator, metadata.Details, metadata.TagNames);
    }

    private static string? ExtractSoundgasmTitle(string html)
    {
        var rawTitle = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'"">]*\bjp-title\b[^'"">]*['""][^>]*>(.*?)</div>");
        if (string.IsNullOrWhiteSpace(rawTitle))
            return null;

        return OfficialDownloaderUtilities.CleanHtmlFragment(rawTitle);
    }

    private static string CleanAudioTitle(string? title, string url)
    {
        var fallback = OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Audio");
        if (string.IsNullOrWhiteSpace(title))
            return fallback;

        var trimmed = title.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && trimmed.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)
            ? fallback
            : trimmed;
    }

    private static string? ExtractAudioCreator(string html, string pageUrl)
    {
        var metaAuthor = OfficialDownloaderUtilities.ExtractMetaContent(html, "author")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "article:author");
        if (!string.IsNullOrWhiteSpace(metaAuthor))
            return metaAuthor.Trim();

        var audioBy = ExtractSoundgasmAudioBy(html);
        if (!string.IsNullOrWhiteSpace(audioBy))
            return audioBy;

        var profileLink = ExtractSoundgasmProfileLabel(html);
        if (!string.IsNullOrWhiteSpace(profileLink))
            return profileLink;

        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return null;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && segments[0].Equals("u", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(segments[1]).Trim();

        return null;
    }

    private static string? ExtractSoundgasmAudioBy(string html)
    {
        var rawAudioBy = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<b\b[^>]*>\s*Audio\s+by\s*:\s*</b>\s*(.*?)(?:<br\b|</p>|$)");
        if (string.IsNullOrWhiteSpace(rawAudioBy))
            return null;

        var creator = OfficialDownloaderUtilities.CleanHtmlFragment(rawAudioBy).Trim();
        var parenthesized = Regex.Match(creator, @"\((?<name>[^()]+)\)\s*$");
        if (parenthesized.Success && !string.IsNullOrWhiteSpace(parenthesized.Groups["name"].Value))
            return parenthesized.Groups["name"].Value.Trim();

        return string.IsNullOrWhiteSpace(creator) ? null : creator;
    }

    private static string? ExtractSoundgasmProfileLabel(string html)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*(['""])[^'"" >]*?/u/[^'"" >]+\1[^>]*>(?<label>.*?)</a>"))
        {
            var label = OfficialDownloaderUtilities.CleanHtmlFragment(match.Groups["label"].Value);
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        return null;
    }

    private static string? ExtractAudioDescription(string html)
    {
        var rawDescription = OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<div\b[^>]*class\s*=\s*['""][^'"">]*\bjp-description\b[^'"">]*['""][^>]*>(.*?)</div>")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "description")
            ?? OfficialDownloaderUtilities.ExtractMetaContent(html, "og:description");

        if (string.IsNullOrWhiteSpace(rawDescription))
            return null;

        var details = OfficialDownloaderUtilities.CleanHtmlFragment(rawDescription);
        return string.IsNullOrWhiteSpace(details) ? null : details;
    }

    private static string? ExtractAudioUrl(string html, string pageUrl)
    {
        var normalizedHtml = NormalizeEmbeddedUrls(html);
        var candidates = new List<string?>
        {
            OfficialDownloaderUtilities.ExtractMetaContent(html, "og:audio"),
            OfficialDownloaderUtilities.ExtractMetaContent(html, "og:audio:url"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<audio[^>]+src\s*=\s*['""]([^'""]+)['""]"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]"),
            OfficialDownloaderUtilities.ExtractFirstRawMatch(html, @"(?is)(https?://[^'""<>\s]+\.(?:mp3|m4a|ogg|opus|wav|flac)(?:\?[^'""<>\s]+)?)"),
        };

        if (!string.Equals(normalizedHtml, html, StringComparison.Ordinal))
        {
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)<audio[^>]+src\s*=\s*['""]([^'""]+)['""]"));
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)<source[^>]+src\s*=\s*['""]([^'""]+)['""]"));
            candidates.Add(OfficialDownloaderUtilities.ExtractFirstRawMatch(normalizedHtml, @"(?is)(https?://[^'""<>\s]+\.(?:mp3|m4a|ogg|opus|wav|flac)(?:\?[^'""<>\s]+)?)"));
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
                return absolute.ToString();

            if (Uri.TryCreate(new Uri(pageUrl), candidate, out var relative))
                return relative.ToString();
        }

        return null;
    }

    private static string NormalizeEmbeddedUrls(string html)
    {
        return WebUtility.HtmlDecode(html)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u003D", "=", StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient GetHttpClient()
    {
        if (_services == null)
            throw new InvalidOperationException("The common audio downloader has not been initialized yet.");

        return _services.GetRequiredService<IHttpClientFactory>().CreateClient();
    }

    private sealed record AudioPageInfo(string PageUrl, string Title, string AudioUrl, string? Creator, string? Details, List<string> TagNames);
}
