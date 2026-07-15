using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public sealed class MissAV123ScraperExtension : IScraperProvider
{
    private const string ExtensionId = "cove.community.scrapers.missav123";
    private const string VideoScraperId = "cove.community.scrapers.missav123/video";

    private const string CurrentHost = "123av.com";

    private static readonly string[] UrlPatterns = ["missav123.to/*", "*.missav123.to/*", "123av.com/*", "*.123av.com/*"];

    private static readonly ScraperDescriptor VideoScraper = new(
        VideoScraperId,
        "MissAV123 Video",
        ScraperEntity.Video,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName | ScraperCapabilities.ByFragment | ScraperCapabilities.ByQueryFragment,
        UrlPatterns,
        ScraperRiskLevel.NetworkOnly,
        ["123av.com", "missav123.to"]);

    private IServiceProvider? _services;

    public string Id => ExtensionId;
    public string Name => "MissAV123 Scraper";
    public string Version => "1.0.0";
    public string? Description => "Extracts scene metadata from MissAV123/123AV pages.";
    public string? Author => null;
    public string? Url => "https://github.com/yourcove/communityscrapers";
    public string? IconUrl => null;
    public IReadOnlyList<string> Categories => [ExtensionCategories.Scraper, ExtensionCategories.Metadata, "video"];

    public void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
    }

    public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [VideoScraper];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(ScraperRequest<VideoScrapeInput> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, VideoScraperId, StringComparison.OrdinalIgnoreCase))
            return null;

        var url = ResolveVideoUrl(request.Input);
        if (string.IsNullOrWhiteSpace(url) || !IsMissAV123Url(url))
            return null;

        var html = await GetStringAsync(url, ct);
        return ParseVideo(html, url);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, VideoScraperId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var code = ExtractCode(request.Input);
        if (!string.IsNullOrWhiteSpace(code))
        {
            try
            {
                var directUrl = BuildVideoUrl(code);
                var directHtml = await GetStringAsync(directUrl, ct);
                var directResult = ParseVideo(directHtml, directUrl);
                if (directResult != null)
                    return [directResult];
            }
            catch (HttpRequestException)
            {
            }
        }

        var url = $"https://{CurrentHost}/en/search?keyword={Uri.EscapeDataString(request.Input.Trim())}";
        var html = await GetStringAsync(url, ct);
        return ParseSearch(html).ToList();
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
        request.Headers.AcceptLanguage.ParseAdd("en-US");
        request.Headers.AcceptLanguage.ParseAdd("en;q=0.9");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private HttpClient GetHttpClient()
        => _services?.GetRequiredService<IHttpClientFactory>().CreateClient(Id)
            ?? throw new InvalidOperationException("The MissAV123 scraper has not been initialized.");

    private static ScrapedVideoDto? ParseVideo(string html, string fallbackUrl)
    {
        var canonicalUrl = ExtractDTagAttribute(html, "WatchShareBox", "url") ?? ExtractLinkHref(html, "canonical") ?? fallbackUrl;
        canonicalUrl = NormalizeKnownUrl(canonicalUrl);
        var details = ExtractTextReadMore(html);
        var genreTags = ExtractAnchorTextsFromInfoRow(html, "Genres");
        var extraTags = ExtractAnchorTextsFromInfoRow(html, "Tags");
        var seriesTags = ExtractAnchorTextsFromInfoRow(html, "Series");
        var tags = genreTags.Concat(extraTags).Concat(seriesTags).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var performers = ExtractAnchorTextsFromInfoRow(html, "Cast");
        var studio = ExtractAnchorTextsFromInfoRow(html, "Maker").FirstOrDefault();

        var title = ExtractElementByExactClass(html, "h1", "watch__title")
            ?? ExtractElementByExactClass(html, "h1", "title")
            ?? CleanTitle(ExtractHtmlTitle(html));
        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(canonicalUrl)
            && performers.Count == 0
            && tags.Count == 0
            && string.IsNullOrWhiteSpace(studio))
        {
            return null;
        }

        return new ScrapedVideoDto
        {
            SourceScraperId = VideoScraperId,
            Title = title,
            Urls = string.IsNullOrWhiteSpace(canonicalUrl) ? [fallbackUrl] : [canonicalUrl],
            Code = ExtractCode(canonicalUrl) ?? ExtractCode(fallbackUrl),
            Date = ParseDate(ExtractTextFromInfoRow(html, "Release date") ?? ExtractTextFromMetaLabel(html, "Released date:")),
            Details = details,
            ImageUrl = ExtractImageUrl(html),
            StudioName = studio,
            PerformerNames = performers,
            TagNames = tags,
        };
    }

    private static IReadOnlyList<ScrapedVideoDto> ParseSearch(string html)
    {
        var results = new List<ScrapedVideoDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match block in Regex.Matches(html, @"(?is)<div\b(?=[^>]*\bclass\s*=\s*(['""])[^'""]*\bcard\b[^'""]*\1)[^>]*>(?<content>.*?)(?=<div\b[^>]*\bclass\s*=\s*(['""])[^'""]*\bcard\b|</main>|<footer\b|$)"))
        {
            var content = block.Groups["content"].Value;
            var href = ExtractAnchorHrefByClassFragment(content, "card__cover") ?? ExtractAnchorHrefByClassFragment(content, "card__link");
            var url = ToAbsoluteUrl(href);
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                continue;

            var titleAnchor = ExtractAnchorContentByClassFragment(content, "card__link");
            var title = FirstNonEmpty(CleanHtml(titleAnchor ?? string.Empty), ExtractCode(url));
            results.Add(new ScrapedVideoDto
            {
                SourceScraperId = VideoScraperId,
                Title = title,
                Urls = [url],
                Code = ExtractCode(url),
                ImageUrl = ExtractFirstRawMatch(content, @"(?is)<img\b[^>]*\bsrc\s*=\s*(['""])(?<value>.*?)\1"),
            });
        }

        return results;
    }

    private static string? ResolveVideoUrl(VideoScrapeInput input)
    {
        var url = input.Url ?? input.Urls.FirstOrDefault(IsMissAV123Url);
        if (!string.IsNullOrWhiteSpace(url))
            return url.Trim();

        var code = FirstNonEmpty(input.Code, ExtractCode(input.Title));
        if (string.IsNullOrWhiteSpace(code))
        {
            foreach (var file in input.Files)
            {
                code = ExtractCode(file.Path);
                if (!string.IsNullOrWhiteSpace(code))
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(code) ? null : BuildVideoUrl(code);
    }

    private static bool IsMissAV123Url(string? url)
        => !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (IsHost(uri, "missav123.to") || IsHost(uri, CurrentHost));

    private static string BuildVideoUrl(string code) => $"https://{CurrentHost}/en/v/{NormalizeCode(code)}";

    private static List<string> ExtractAnchorTextsFromInfoRow(string html, string label)
    {
        var content = ExtractInfoRowContent(html, label);
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return Regex.Matches(content, @"(?is)<a\b[^>]*>(?<text>.*?)</a>")
            .Select(match => CleanHtml(match.Groups["text"].Value))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractTextFromInfoRow(string html, string label)
    {
        var content = ExtractInfoRowContent(html, label);
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var dd = ExtractElementText(content, "dd");
        return string.IsNullOrWhiteSpace(dd) ? null : dd;
    }

    private static string? ExtractInfoRowContent(string html, string label)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<div\b(?=[^>]*\bclass\s*=\s*(['""])[^'""]*\bwatch__info-row\b[^'""]*\1)[^>]*>(?<content>.*?)</div>"))
        {
            var content = match.Groups["content"].Value;
            var dt = ExtractElementText(content, "dt");
            if (string.Equals(dt, label, StringComparison.OrdinalIgnoreCase))
                return content;
        }

        return null;
    }

    private static List<string> ExtractAnchorTextsFromMetaLabel(string html, string label)
    {
        var content = ExtractMetaLabelContent(html, label);
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return Regex.Matches(content, @"(?is)<a\b[^>]*>(?<text>.*?)</a>")
            .Select(match => CleanHtml(match.Groups["text"].Value))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractTextFromMetaLabel(string html, string label)
    {
        var content = ExtractMetaLabelContent(html, label);
        return string.IsNullOrWhiteSpace(content) ? null : ExtractElementText(content, "span");
    }

    private static string? ExtractMetaLabelContent(string html, string label)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<div\b[^>]*>(?<content>.*?<label\b[^>]*>.*?</label>.*?)</div>"))
        {
            var content = match.Groups["content"].Value;
            if (Regex.IsMatch(content, $@"(?is)<label\b[^>]*>\s*{Regex.Escape(label)}\s*</label>"))
                return content;
        }

        return null;
    }

    private static string? ExtractTextReadMore(string html)
    {
        var match = Regex.Match(html, @"(?is)<d-tag\b(?=[^>]*\bsrc\s*=\s*(['""])TextReadMore\1)[^>]*>.*?<div\b(?=[^>]*\bclass\s*=\s*(['""])content\2)[^>]*>(?<text>.*?)</div>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractImageUrl(string html)
    {
        var cover = ExtractDTagAttributeById(html, "player", "cover");
        if (!string.IsNullOrWhiteSpace(cover))
            return cover;

        foreach (Match match in Regex.Matches(html, @"(?is)<div\b(?=[^>]*(?:background-image|plyr__poster|player))[^>]*\bstyle\s*=\s*(['""])(?<style>.*?)\1"))
        {
            var style = WebUtility.HtmlDecode(match.Groups["style"].Value);
            var urlMatch = Regex.Match(style, @"(?i)url\(['""]?(?<value>[^'"")]+)['""]?\)");
            if (urlMatch.Success)
                return urlMatch.Groups["value"].Value.Trim();
        }

        return ExtractFirstRawMatch(html, @"(?is)<iframe\b[^>]*\bposter\s*=\s*(['""])(?<value>.*?)\1");
    }

    private static string? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = CleanHtml(value);
        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return cleaned;
    }

    private static string NormalizeCode(string code) => code.Trim().ToLowerInvariant();

    private static string? ExtractCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = Regex.Match(value.ToUpperInvariant(), @"(?<code>[A-Z]+-\d+)|(?<prefix>[A-Z]+)(?<number>\d+)");
        if (!match.Success)
            return null;

        return match.Groups["code"].Success
            ? match.Groups["code"].Value
            : $"{match.Groups["prefix"].Value}-{match.Groups["number"].Value}";
    }

    private static string? ExtractDTagAttribute(string html, string src, string attributeName)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<d-tag\b(?<attrs>[^>]*)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\bsrc\s*=\s*(['""]){Regex.Escape(src)}\1"))
                continue;

            var value = ExtractAttribute(attrs, attributeName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ExtractDTagAttributeById(string html, string id, string attributeName)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<d-tag\b(?<attrs>[^>]*)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\bid\s*=\s*(['""]){Regex.Escape(id)}\1"))
                continue;

            var value = ExtractAttribute(attrs, attributeName);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ExtractLinkHref(string html, string rel)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<link\b(?<attrs>[^>]+)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\brel\s*=\s*(['""]){Regex.Escape(rel)}\1"))
                continue;

            var href = ExtractAttribute(attrs, "href");
            if (!string.IsNullOrWhiteSpace(href))
                return WebUtility.HtmlDecode(href).Trim();
        }

        return null;
    }

    private static string? ExtractElementByExactClass(string html, string tagName, string className)
    {
        var match = Regex.Match(html, $@"(?is)<{Regex.Escape(tagName)}\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\b{Regex.Escape(className)}\b[^'""]*['""])[^>]*>(?<text>.*?)</{Regex.Escape(tagName)}>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractHtmlTitle(string html)
        => ExtractElementText(html, "title");

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        return Regex.Replace(title, @"\s+—\s+123AV\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static string? ExtractElementText(string html, string tagName)
    {
        var match = Regex.Match(html, $@"(?is)<{Regex.Escape(tagName)}\b[^>]*>(?<text>.*?)</{Regex.Escape(tagName)}>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractAnchorHrefByClassFragment(string html, string className)
    {
        var match = Regex.Match(html, $@"(?is)<a\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\b{Regex.Escape(className)}\b[^'""]*['""])(?<attrs>[^>]*)>");
        return match.Success ? ExtractAttribute(match.Groups["attrs"].Value, "href") : null;
    }

    private static string? ExtractAnchorContentByClassFragment(string html, string className)
    {
        var match = Regex.Match(html, $@"(?is)<a\b(?=[^>]*\bclass\s*=\s*['""][^'""]*\b{Regex.Escape(className)}\b[^'""]*['""])[^>]*>(?<text>.*?)</a>");
        return match.Success ? match.Groups["text"].Value : null;
    }

    private static string? ExtractFirstRawMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

    private static string? ExtractAttribute(string text, string attributeName)
    {
        var match = Regex.Match(text, $@"(?i)\b{Regex.Escape(attributeName)}\s*=\s*(['""])(?<value>.*?)\1");
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

    private static string CleanHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, @"(?is)<(script|style)\b.*?</\1>", " ");
        var withoutTags = Regex.Replace(withoutScripts, @"(?is)<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? ToAbsoluteUrl(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        var decoded = WebUtility.HtmlDecode(href).Trim();
        if (decoded.StartsWith("//", StringComparison.Ordinal))
            return "https:" + decoded;
        if (decoded.StartsWith("/", StringComparison.Ordinal))
            return $"https://{CurrentHost}" + decoded;

        return Uri.TryCreate(decoded, UriKind.Absolute, out _) ? NormalizeKnownUrl(decoded) : $"https://{CurrentHost}/{decoded.TrimStart('/')}";
    }

    private static string? NormalizeKnownUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return url.Trim();

        return IsHost(uri, "missav123.to")
            ? new UriBuilder(uri) { Host = CurrentHost, Scheme = "https", Port = -1 }.Uri.ToString()
            : url.Trim();
    }

    private static bool IsHost(Uri uri, string host)
        => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
