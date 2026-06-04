// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/FAKNetwork/scrape.py; scrapers/FAKNetwork/sites.py
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public abstract class FakNetworkScraperBase : IScraperProvider
{
    private static readonly string[] Languages = ["en", "es", "pt"];

    private readonly string _extensionId;
    private readonly string _videoScraperId;
    private readonly string _extensionName;
    private readonly string _siteKey;
    private readonly string _siteDisplayName;
    private readonly string _siteHost;
    private readonly ScraperDescriptor _videoScraper;
    private IServiceProvider? _services;

    protected FakNetworkScraperBase(string extensionId, string extensionName, string siteKey, string siteDisplayName)
    {
        _extensionId = extensionId;
        _videoScraperId = extensionId + "/video";
        _extensionName = extensionName;
        _siteKey = siteKey;
        _siteDisplayName = siteDisplayName;
        _siteHost = siteKey + ".com";

        var supportedUrls = BuildSupportedUrls(_siteHost);
        _videoScraper = new ScraperDescriptor(
            _videoScraperId,
            siteDisplayName + " Video",
            ScraperEntity.Video,
            ScraperCapabilities.ByUrl | ScraperCapabilities.ByName | ScraperCapabilities.ByFragment | ScraperCapabilities.ByQueryFragment,
            supportedUrls,
            ScraperRiskLevel.NetworkOnly,
            [_siteHost, "api.faknetworks.com"]);
    }

    public string Id => _extensionId;
    public string Name => _extensionName;
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(GetType());
    public string? Description => $"Extracts scene metadata from {_siteDisplayName} pages.";
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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [_videoScraper];

    public async Task<ScrapedVideoDto?> ScrapeVideoAsync(ScraperRequest<VideoScrapeInput> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, _videoScraperId, StringComparison.OrdinalIgnoreCase))
            return null;

        var lookup = ResolveSceneLookup(request.Input);
        if (lookup == null)
            return null;

        return await FetchSceneAsync(lookup.Value.Slug, lookup.Value.Language, ct);
    }

    public async Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, _videoScraperId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var apiUrl = "https://api.faknetworks.com/v1/search"
            + $"?query={Uri.EscapeDataString(request.Input.Trim())}"
            + $"&product={Uri.EscapeDataString(_siteKey)}&lang=en&limit=10";
        var json = await GetStringAsync(apiUrl, ct);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<ScrapedVideoDto>();
        foreach (var video in videos.EnumerateArray())
        {
            var scene = await ConvertSceneAsync(video, "en", ct);
            if (scene != null)
                results.Add(scene);
        }

        return results;
    }

    private async Task<ScrapedVideoDto?> FetchSceneAsync(string slug, string language, CancellationToken ct)
    {
        var apiUrl = $"https://api.faknetworks.com/v1/public/videos/{Uri.EscapeDataString(slug)}?lang={Uri.EscapeDataString(language)}";
        var json = await GetStringAsync(apiUrl, ct);

        using var document = JsonDocument.Parse(json);
        return await ConvertSceneAsync(document.RootElement, language, ct);
    }

    private async Task<ScrapedVideoDto?> ConvertSceneAsync(JsonElement data, string fallbackLanguage, CancellationToken ct)
    {
        var title = GetJsonString(data, "title");
        var slug = GetJsonString(data, "slug");
        var product = GetJsonString(data, "product") ?? _siteKey;
        var language = NormalizeLanguage(fallbackLanguage);
        var tagMap = await GetCategoryTitlesAsync(product, language, ct);

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(slug))
            return null;

        return new ScrapedVideoDto
        {
            SourceScraperId = _videoScraperId,
            Title = title,
            Date = ParseDate(GetJsonString(data, "date")),
            Code = GetJsonString(data, "filename") ?? slug,
            Details = CleanHtml(GetJsonString(data, "description")),
            ImageUrl = BuildImageUrl(GetJsonString(data, "horizontalProfile")),
            StudioName = ExtractStudioName(data),
            PerformerNames = ExtractPerformerNames(data),
            TagNames = ExtractTagNames(data, tagMap),
            Urls = string.IsNullOrWhiteSpace(slug) ? [] : [BuildSceneUrl(product, language, slug)],
        };
    }

    private async Task<Dictionary<string, string>> GetCategoryTitlesAsync(string product, string language, CancellationToken ct)
    {
        try
        {
            var apiUrl = "https://api.faknetworks.com/v1/categories"
                + $"?product={Uri.EscapeDataString(product)}&lang={Uri.EscapeDataString(language)}&page=1&take=1000";
            var json = await GetStringAsync(apiUrl, ct);
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return [];

            var mappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var category in results.EnumerateArray())
            {
                var id = GetJsonString(category, "id");
                var title = GetJsonString(category, "title");
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                    mappings[id] = title;
            }

            return mappings;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    private SceneLookup? ResolveSceneLookup(VideoScrapeInput input)
    {
        var directUrl = TryParseSceneUrl(input.Url);
        if (directUrl != null)
            return directUrl;

        foreach (var url in input.Urls)
        {
            var urlLookup = TryParseSceneUrl(url);
            if (urlLookup != null)
                return urlLookup;
        }

        if (!string.IsNullOrWhiteSpace(input.Code))
            return new SceneLookup(input.Code.Trim(), "en");

        foreach (var file in input.Files)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.Path);
            if (!string.IsNullOrWhiteSpace(fileName))
                return new SceneLookup(fileName, "en");
        }

        return null;
    }

    private SceneLookup? TryParseSceneUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return null;

        if (!IsSupportedHost(uri.Host))
            return null;

        var match = Regex.Match(uri.AbsolutePath, @"^/(?:(?<language>[a-z]{2})/)?video/(?<slug>[-\w]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var slug = match.Groups["slug"].Value;
        var language = NormalizeLanguage(match.Groups["language"].Value);
        return string.IsNullOrWhiteSpace(slug) ? null : new SceneLookup(slug, language);
    }

    private bool IsSupportedHost(string host)
    {
        return string.Equals(host, _siteHost, StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "www." + _siteHost, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CoveFakNetworkScraper/1.0");
        request.Headers.Accept.ParseAdd("application/json,text/html;q=0.8,*/*;q=0.6");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private HttpClient GetHttpClient()
        => _services?.GetRequiredService<IHttpClientFactory>().CreateClient(Id)
            ?? throw new InvalidOperationException("The FAK Network scraper has not been initialized.");

    private string ExtractStudioName(JsonElement data)
    {
        if (data.TryGetProperty("serie", out var series) && series.ValueKind == JsonValueKind.Object)
            return GetJsonString(series, "title", "name") ?? _siteDisplayName;

        return _siteDisplayName;
    }

    private static List<string> ExtractPerformerNames(JsonElement data)
    {
        if (!data.TryGetProperty("performers", out var performers) || performers.ValueKind != JsonValueKind.Array)
            return [];

        return performers.EnumerateArray()
            .Select(performer => GetJsonString(performer, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractTagNames(JsonElement data, IReadOnlyDictionary<string, string> tagMap)
    {
        if (!data.TryGetProperty("categories", out var categories) || categories.ValueKind != JsonValueKind.Array)
            return [];

        return categories.EnumerateArray()
            .Select(category =>
            {
                var id = GetJsonString(category, "id");
                if (!string.IsNullOrWhiteSpace(id) && tagMap.TryGetValue(id, out var mapped))
                    return mapped;

                return GetJsonString(category, "title", "name");
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static string NormalizeLanguage(string? language)
        => Languages.Contains(language, StringComparer.OrdinalIgnoreCase) ? language!.ToLowerInvariant() : "en";

    private static string? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var datePrefix = Regex.Match(value, @"^\s*(?<date>\d{4}-\d{2}-\d{2})");
        if (datePrefix.Success)
            return datePrefix.Groups["date"].Value;

        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value.Trim();
    }

    private static string? CleanHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var withoutTags = Regex.Replace(value, @"(?is)<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        var cleaned = Regex.Replace(decoded, @"\s+", " ").Trim();
        cleaned = Regex.Replace(cleaned, @"\s+([.,;:!?])", "$1");
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static string? BuildImageUrl(string? image)
    {
        if (string.IsNullOrWhiteSpace(image))
            return null;

        if (Uri.TryCreate(image, UriKind.Absolute, out _))
            return image;

        return "https://player.faknetworks.com/almacen/videos/listado_horizontal_" + image.TrimStart('/');
    }

    private static string BuildSceneUrl(string product, string language, string slug)
        => $"https://{product}.com/{language}/video/{slug}";

    private static IReadOnlyList<string> BuildSupportedUrls(string host)
    {
        var urls = new List<string> { host + "/video/*", "www." + host + "/video/*" };
        foreach (var language in Languages)
        {
            urls.Add(host + "/" + language + "/video/*");
            urls.Add("www." + host + "/" + language + "/video/*");
        }

        return urls;
    }

    private readonly record struct SceneLookup(string Slug, string Language);
}