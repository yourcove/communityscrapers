// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/Pornhub/Pornhub.yml; scrapers/Pornhub/PornhubStar.py
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public sealed class PornhubScraperExtension : IScraperProvider
{
    private const string ExtensionId = "cove.community.scrapers.pornhub";
    private const string SceneScraperId = "cove.community.scrapers.pornhub/scene";
    private const string PerformerScraperId = "cove.community.scrapers.pornhub/performer";

    private static readonly string[] PornhubUrlPatterns = ["pornhub.com/*", "*.pornhub.com/*", "pornhub.org/*", "*.pornhub.org/*"];

    private static readonly ScraperDescriptor SceneScraper = new(
        SceneScraperId,
        "Pornhub Scene",
        ScraperEntity.Scene,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName | ScraperCapabilities.ByFragment | ScraperCapabilities.ByQueryFragment,
        PornhubUrlPatterns,
        ScraperRiskLevel.NetworkOnly,
        ["pornhub.com", "pornhub.org"]);

    private static readonly ScraperDescriptor PerformerScraper = new(
        PerformerScraperId,
        "Pornhub Performer",
        ScraperEntity.Performer,
        ScraperCapabilities.ByUrl | ScraperCapabilities.ByName,
        PornhubUrlPatterns,
        ScraperRiskLevel.NetworkOnly,
        ["pornhub.com", "pornhub.org"]);

    private IServiceProvider? _services;

    public string Id => ExtensionId;
    public string Name => "Pornhub Scraper";
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(typeof(PornhubScraperExtension));
    public string? Description => "Extracts scene and performer metadata from Pornhub pages.";
    public string? Author => null;
    public string? Url => OfficialDownloaderUtilities.RepoUrl;
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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [SceneScraper, PerformerScraper];

    public async Task<ScrapedSceneDto?> ScrapeSceneAsync(ScraperRequest<SceneScrapeInput> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, SceneScraperId, StringComparison.OrdinalIgnoreCase))
            return null;

        var url = ResolveSceneUrl(request.Input);
        if (string.IsNullOrWhiteSpace(url) || !IsPornhubUrl(url))
            return null;

        var html = await GetStringAsync(url, ct);
        return ParseScene(html, url);
    }

    public async Task<IReadOnlyList<ScrapedSceneDto>> SearchScenesAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, SceneScraperId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var url = $"https://www.pornhub.com/video/search?search={Uri.EscapeDataString(request.Input.Trim())}";
        var html = await GetStringAsync(url, ct);
        return ParseSceneSearch(html).ToList();
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(ScraperRequest<PerformerScrapeInput> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, PerformerScraperId, StringComparison.OrdinalIgnoreCase))
            return null;

        var url = ResolvePerformerUrl(request.Input);
        if (string.IsNullOrWhiteSpace(url) || !IsPornhubUrl(url))
            return null;

        var html = await GetStringAsync(url, ct);
        return ParsePerformer(html, url);
    }

    public async Task<IReadOnlyList<ScrapedPerformerDto>> SearchPerformersAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, PerformerScraperId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var token = ExtractToken(await GetStringAsync("https://www.pornhub.com", ct));
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Pornhub performer search token could not be found.");

        var apiUrl = "https://www.pornhub.com/api/v1/video/search_autocomplete"
            + $"?q={Uri.EscapeDataString(request.Input.Trim())}"
            + $"&token={Uri.EscapeDataString(token)}&pornstars=true&alt=0";
        var json = await GetStringAsync(apiUrl, ct);
        return ParsePerformerSearch(json);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CovePornhubScraper/1.0");
        request.Headers.TryAddWithoutValidation("Cookie", "accessAgeDisclaimerPH=1");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private HttpClient GetHttpClient()
        => _services?.GetRequiredService<IHttpClientFactory>().CreateClient(Id)
            ?? throw new InvalidOperationException("The Pornhub scraper has not been initialized.");

    private static ScrapedSceneDto? ParseScene(string html, string fallbackUrl)
    {
        var title = ExtractElementText(html, "h1");
        if (string.Equals(title, "Recently Featured Porn Videos", StringComparison.OrdinalIgnoreCase))
            title = null;

        var canonicalUrl = ExtractMetaContent(html, "og:url") ?? fallbackUrl;
        var date = ParseSceneDate(html);
        var performers = ExtractLinkTextsByDataLabel(html, "pornstar");
        var tags = ExtractLinkTextsByDataLabel(html, "category")
            .Concat(ExtractLinkTextsByDataLabel(html, "tag"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var studio = ExtractLinkTextsByDataLabel(html, "channel").FirstOrDefault()
            ?? ExtractStudioButton(html);

        if (string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(canonicalUrl)
            && performers.Count == 0
            && tags.Count == 0)
        {
            return null;
        }

        return new ScrapedSceneDto
        {
            SourceScraperId = SceneScraperId,
            Title = title,
            Urls = string.IsNullOrWhiteSpace(canonicalUrl) ? [] : [canonicalUrl],
            Date = date,
            Code = ExtractViewKey(canonicalUrl),
            ImageUrl = ExtractMetaContent(html, "og:image"),
            StudioName = studio,
            PerformerNames = performers,
            TagNames = tags,
        };
    }

    private static IReadOnlyList<ScrapedSceneDto> ParseSceneSearch(string html)
    {
        var results = new List<ScrapedSceneDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(html, @"(?is)<a\b[^>]*href\s*=\s*(['""'])(?<href>[^'""#]*view_video\.php\?viewkey=[^'""]+)\1[^>]*>(?<text>.*?)</a>"))
        {
            var url = ToAbsolutePornhubUrl(match.Groups["href"].Value);
            if (string.IsNullOrWhiteSpace(url) || !seen.Add(url))
                continue;

            var title = CleanHtml(match.Groups["text"].Value);
            results.Add(new ScrapedSceneDto
            {
                SourceScraperId = SceneScraperId,
                Title = string.IsNullOrWhiteSpace(title) ? OfficialDownloaderUtilities.DeriveTitleFromUrl(url, "Pornhub scene") : title,
                Urls = [url],
                Code = ExtractViewKey(url),
            });
        }

        return results;
    }

    private static ScrapedPerformerDto? ParsePerformer(string html, string fallbackUrl)
    {
        var canonicalUrl = ExtractLinkHref(html, "canonical") ?? fallbackUrl;
        var name = ExtractElementText(html, "h1") ?? CleanTitle(ExtractHtmlTitle(html));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var birthdate = ParseDate(ExtractItemPropContent(html, "birthDate") ?? ExtractInfoValue(html, "Born:"));
        var country = NormalizeCountry(ExtractInfoValue(html, "Birthplace:")
            ?? ExtractInfoValue(html, "City and Country:")
            ?? ExtractInfoValue(html, "Birth Place:"));
        var gender = NormalizeGender(ExtractInfoValue(html, "Gender:"));

        return new ScrapedPerformerDto
        {
            SourceScraperId = PerformerScraperId,
            Name = name,
            Birthdate = birthdate,
            Country = country,
            Gender = gender,
            Measurements = ExtractInfoValue(html, "Measurements:"),
            Weight = ParseInteger(ExtractMetricValue(html, "Weight:", "kg")),
            HeightCm = ParseInteger(ExtractMetricValue(html, "Height:", "cm")),
            Details = ExtractDescription(html),
            Ethnicity = ExtractInfoValue(html, "Ethnicity:"),
            Piercings = ExtractInfoValue(html, "Piercings:"),
            Tattoos = ExtractInfoValue(html, "Tattoos:"),
            HairColor = ExtractInfoValue(html, "Hair Color:"),
            ImageUrl = ExtractImageUrl(html),
            Urls = string.IsNullOrWhiteSpace(canonicalUrl) ? [fallbackUrl] : [canonicalUrl],
        };
    }

    private static IReadOnlyList<ScrapedPerformerDto> ParsePerformerSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var results = new List<ScrapedPerformerDto>();
        AddPerformerSearchResults(document.RootElement, "models", "model", results);
        AddPerformerSearchResults(document.RootElement, "pornstars", "pornstar", results);

        return results
            .Where(result => !string.IsNullOrWhiteSpace(result.Name) && result.Urls.Count > 0)
            .OrderBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddPerformerSearchResults(JsonElement root, string propertyName, string route, List<ScrapedPerformerDto> results)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in array.EnumerateArray())
        {
            var name = GetJsonString(item, "name");
            var slug = GetJsonString(item, "slug");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(slug))
                continue;

            results.Add(new ScrapedPerformerDto
            {
                SourceScraperId = PerformerScraperId,
                Name = name,
                Urls = [$"https://www.pornhub.com/{route}/{slug}"],
            });
        }
    }

    private static string? ResolveSceneUrl(SceneScrapeInput input)
    {
        var url = input.Url ?? input.Urls.FirstOrDefault(IsPornhubUrl);
        if (!string.IsNullOrWhiteSpace(url))
            return url.Trim();

        var code = FirstNonEmpty(input.Code, ExtractViewKey(input.Title));
        if (string.IsNullOrWhiteSpace(code))
        {
            foreach (var file in input.Files)
            {
                code = ExtractViewKey(file.Path);
                if (!string.IsNullOrWhiteSpace(code))
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(code) ? null : $"https://www.pornhub.com/view_video.php?viewkey={code}";
    }

    private static string? ResolvePerformerUrl(PerformerScrapeInput input)
        => input.Url ?? input.Urls.FirstOrDefault(IsPornhubUrl);

    private static bool IsPornhubUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (OfficialDownloaderUtilities.IsHost(uri, "pornhub.com") || OfficialDownloaderUtilities.IsHost(uri, "pornhub.org"));
    }

    private static string? ExtractToken(string html)
        => ExtractFirstRawMatch(html, "data-token\\s*=\\s*(['\"])(?<value>[^'\"]+)\\1");

    private static string? ExtractViewKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var queryMatch = Regex.Match(value, @"(?i)(?:viewkey=|[?&]v=)(?<id>[A-Za-z0-9]+)");
        if (queryMatch.Success)
            return queryMatch.Groups["id"].Value;

        var fileMatch = Regex.Match(value, @"(?i)(?:^|[^A-Za-z0-9])(?<id>(?:ph)?[A-Za-z0-9]{13})(?:[^A-Za-z0-9]|$)");
        return fileMatch.Success ? fileMatch.Groups["id"].Value : null;
    }

    private static string? ParseSceneDate(string html)
    {
        var compactDate = ExtractFirstRawMatch(html, @"video_date_published['""\s:]+(?<value>\d{8})");
        if (DateTime.TryParseExact(compactDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedCompact))
            return parsedCompact.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var jsonDate = ExtractFirstRawMatch(html, @"(?i)uploadDate\s*['""\s:]+(?<value>\d{4}-\d{2}-\d{2})");
        return ParseDate(jsonDate);
    }

    private static string? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = CleanHtml(value);
        if (DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return null;
    }

    private static string? ExtractInfoValue(string html, string label)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<div\b(?=[^>]*\bclass\s*=\s*(['""'])[^'""'>]*\binfoPiece\b[^'""'>]*\1)[^>]*>(?<content>.*?)</div>"))
        {
            var text = CleanHtml(match.Groups["content"].Value);
            if (!text.Contains(label, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Regex.Replace(text, $"(?i)^.*?{Regex.Escape(label)}", string.Empty).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? ExtractMetricValue(string html, string label, string metric)
    {
        var value = ExtractInfoValue(html, label);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var metricMatch = Regex.Match(value, $@"(?i)\((?<value>\d+)\s*{Regex.Escape(metric)}\)");
        if (metricMatch.Success)
            return metricMatch.Groups["value"].Value;

        metricMatch = Regex.Match(value, $@"(?i)(?<value>\d+)\s*{Regex.Escape(metric)}");
        return metricMatch.Success ? metricMatch.Groups["value"].Value : null;
    }

    private static string? NormalizeCountry(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var country = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? value.Trim();
        return country switch
        {
            "US" => "USA",
            "United States of America" => "USA",
            _ => country,
        };
    }

    private static string? NormalizeGender(string? value)
    {
        return value switch
        {
            "Trans Man" => "transgender_male",
            "Trans Woman" => "transgender_female",
            _ => value,
        };
    }

    private static string? ExtractDescription(string html)
        => ExtractElementByAttribute(html, "itemprop", "description")
            ?? ExtractElementByClassFragment(html, "longBio");

    private static string? ExtractImageUrl(string html)
        => ExtractFirstRawMatch(html, @"(?is)<div\b(?=[^>]*\bclass\s*=\s*(['""'])[^'""'>]*\bthumbImage\b[^'""'>]*\1)[^>]*>.*?<img\b[^>]*\bsrc\s*=\s*(['""'])(?<value>[^'""]+)\2")
            ?? ExtractFirstRawMatch(html, @"(?is)<img\b(?=[^>]*\bid\s*=\s*(['""'])getAvatar\1)[^>]*\bsrc\s*=\s*(['""'])(?<value>[^'""]+)\2");

    private static string? ExtractStudioButton(string html)
    {
        var match = Regex.Match(html, @"(?is)<span\b(?=[^>]*usernameBadgesWrapper)[^>]*>.*?<a\b[^>]*>(?<text>.*?)</a>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static List<string> ExtractLinkTextsByDataLabel(string html, string label)
    {
        return Regex.Matches(html, $@"(?is)<a\b(?=[^>]*\bdata-label\s*=\s*(['""']){Regex.Escape(label)}\1)[^>]*>(?<text>.*?)</a>")
            .Select(match => CleanHtml(match.Groups["text"].Value))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractMetaContent(string html, string propertyOrName)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<meta\b(?<attrs>[^>]+)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\b(?:property|name)\s*=\s*(['""']){Regex.Escape(propertyOrName)}\1"))
                continue;

            var content = ExtractAttribute(attrs, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return WebUtility.HtmlDecode(content).Trim();
        }

        return null;
    }

    private static string? ExtractItemPropContent(string html, string itemProp)
    {
        foreach (Match match in Regex.Matches(html, $@"(?is)<[^>]+\bitemprop\s*=\s*(['""']){Regex.Escape(itemProp)}\1[^>]*>"))
        {
            var content = ExtractAttribute(match.Value, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return WebUtility.HtmlDecode(content).Trim();
        }

        return ExtractElementByAttribute(html, "itemprop", itemProp);
    }

    private static string? ExtractLinkHref(string html, string rel)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<link\b(?<attrs>[^>]+)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\brel\s*=\s*(['""']){Regex.Escape(rel)}\1"))
                continue;

            var href = ExtractAttribute(attrs, "href");
            if (!string.IsNullOrWhiteSpace(href))
                return WebUtility.HtmlDecode(href).Trim();
        }

        return null;
    }

    private static string? ExtractHtmlTitle(string html)
        => ExtractElementText(html, "title");

    private static string? CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        return Regex.Replace(title, @"\s*-\s*Pornhub.*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static string? ExtractElementText(string html, string tagName)
    {
        var match = Regex.Match(html, $@"(?is)<{Regex.Escape(tagName)}\b[^>]*>(?<text>.*?)</{Regex.Escape(tagName)}>" );
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractElementByAttribute(string html, string attributeName, string attributeValue)
    {
        var match = Regex.Match(html, $@"(?is)<(?<tag>[a-z0-9]+)\b(?=[^>]*\b{Regex.Escape(attributeName)}\s*=\s*['""']{Regex.Escape(attributeValue)}['""'])[^>]*>(?<text>.*?)</\k<tag>>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractElementByClassFragment(string html, string classFragment)
    {
        var match = Regex.Match(html, $@"(?is)<(?<tag>[a-z0-9]+)\b(?=[^>]*\bclass\s*=\s*['""'][^'""'>]*{Regex.Escape(classFragment)}[^'""'>]*['""'])[^>]*>(?<text>.*?)</\k<tag>>");
        return match.Success ? CleanHtml(match.Groups["text"].Value) : null;
    }

    private static string? ExtractFirstRawMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

    private static string? ExtractAttribute(string text, string attributeName)
    {
        var match = Regex.Match(text, $@"(?i)\b{Regex.Escape(attributeName)}\s*=\s*(['""'])(?<value>.*?)\1");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string CleanHtml(string html)
    {
        var withoutScripts = Regex.Replace(html, @"(?is)<(script|style)\b.*?</\1>", " ");
        var withoutTags = Regex.Replace(withoutScripts, @"(?is)<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? ToAbsolutePornhubUrl(string href)
    {
        var decoded = WebUtility.HtmlDecode(href).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
            return null;

        if (decoded.StartsWith("//", StringComparison.Ordinal))
            return "https:" + decoded;

        if (decoded.StartsWith("/", StringComparison.Ordinal))
            return "https://www.pornhub.com" + decoded;

        return Uri.TryCreate(decoded, UriKind.Absolute, out _) ? decoded : null;
    }

    private static string? GetJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ParseInteger(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
