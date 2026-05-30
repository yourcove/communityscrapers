// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/AyloAPI/scrape.py; scrapers/AyloAPI/domains.py; scrapers/AyloAPI/slugger.py
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public abstract class AyloNetworkScraperBase : IScraperProvider
{
    private const string ApiHost = "site-api.project1service.com";

    private static readonly IReadOnlyDictionary<int, string> TagMap = new Dictionary<int, string>
    {
        [90] = "Athletic Woman",
        [107] = "White Woman",
        [112] = "Black Woman",
        [113] = "European Woman",
        [121] = "Latina Woman",
        [125] = "Black Hair (Female)",
        [126] = "Blond Hair (Female)",
        [127] = "Brown Hair (Female)",
        [128] = "Red Hair (Female)",
        [215] = "Rimming Him",
        [274] = "Rimming Her",
        [374] = "Black Man",
        [376] = "European Man",
        [377] = "Latino Man",
        [378] = "White Man",
        [379] = "Black Hair (Male)",
        [380] = "Blond Hair (Male)",
        [381] = "Brown Hair (Male)",
        [383] = "Red Hair (Male)",
        [385] = "Shaved Head",
        [386] = "Short Hair (Male)",
    };

    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _extensionId;
    private readonly string _extensionName;
    private readonly string _siteDisplayName;
    private readonly string? _studioOverride;
    private readonly IReadOnlyList<string> _domains;
    private readonly ScraperDescriptor _sceneScraper;
    private readonly ScraperDescriptor _galleryScraper;
    private readonly ScraperDescriptor _performerScraper;
    private readonly ScraperDescriptor _groupScraper;
    private IServiceProvider? _services;

    protected AyloNetworkScraperBase(
        string extensionId,
        string extensionName,
        string siteDisplayName,
        IReadOnlyList<string> domains,
        string? studioOverride = null)
    {
        _extensionId = extensionId;
        _extensionName = extensionName;
        _siteDisplayName = siteDisplayName;
        _domains = domains;
        _studioOverride = studioOverride;

        _sceneScraper = new ScraperDescriptor(
            extensionId + "/scene",
            siteDisplayName + " Scene",
            ScraperEntity.Scene,
            ScraperCapabilities.ByUrl | ScraperCapabilities.ByName | ScraperCapabilities.ByFragment | ScraperCapabilities.ByQueryFragment,
            BuildSupportedUrls(domains, ["scene"]),
            ScraperRiskLevel.NetworkOnly,
            BuildPreferenceSites(domains));
        _galleryScraper = new ScraperDescriptor(
            extensionId + "/gallery",
            siteDisplayName + " Gallery",
            ScraperEntity.Gallery,
            ScraperCapabilities.ByUrl | ScraperCapabilities.ByFragment,
            BuildSupportedUrls(domains, ["scene"]),
            ScraperRiskLevel.NetworkOnly,
            BuildPreferenceSites(domains));
        _performerScraper = new ScraperDescriptor(
            extensionId + "/performer",
            siteDisplayName + " Performer",
            ScraperEntity.Performer,
            ScraperCapabilities.ByUrl | ScraperCapabilities.ByName | ScraperCapabilities.ByFragment,
            BuildSupportedUrls(domains, ["model"]),
            ScraperRiskLevel.NetworkOnly,
            BuildPreferenceSites(domains));
        _groupScraper = new ScraperDescriptor(
            extensionId + "/group",
            siteDisplayName + " Group",
            ScraperEntity.Group,
            ScraperCapabilities.ByUrl,
            BuildSupportedUrls(domains, ["scene", "movie"]),
            ScraperRiskLevel.NetworkOnly,
            BuildPreferenceSites(domains));
    }

    public string Id => _extensionId;
    public string Name => _extensionName;
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(GetType());
    public string? Description => $"Extracts scene, performer, gallery, and group metadata from {_siteDisplayName} pages.";
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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [_sceneScraper, _galleryScraper, _performerScraper, _groupScraper];

    public async Task<ScrapedSceneDto?> ScrapeSceneAsync(ScraperRequest<SceneScrapeInput> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "scene"))
            return null;

        var url = FirstUrl(request.Input.Url, request.Input.Urls);
        if (!TryResolveRelease(url, out var release))
            return null;

        var result = await FetchReleaseAsync(release.Domain, release.Id, ct);
        if (GetJsonString(result, "type") != "scene" && TryGetObject(result, "parent", out var parent) && GetJsonString(parent, "type") == "scene")
            result = parent;

        return ToScene(result);
    }

    public async Task<IReadOnlyList<ScrapedSceneDto>> SearchScenesAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "scene") || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var results = new List<ScrapedSceneDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in _domains)
        {
            var searchUrl = $"https://{ApiHost}/v2/releases?search={Uri.EscapeDataString(request.Input.Trim())}&type=scene&limit=10";
            var json = await FetchApiResultAsync(domain, searchUrl, ct);
            if (json.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in json.EnumerateArray())
            {
                if (GetJsonString(item, "type") != "scene")
                    continue;

                var id = GetJsonString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    continue;

                var scene = ToScene(item);
                if (scene != null)
                    results.Add(scene);
            }

            if (results.Count >= 10)
                break;
        }

        return results;
    }

    public async Task<ScrapedGalleryDto?> ScrapeGalleryAsync(ScraperRequest<GalleryScrapeInput> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "gallery"))
            return null;

        var url = FirstUrl(request.Input.Url, request.Input.Urls);
        if (!TryResolveRelease(url, out var release))
            return null;

        var result = await FetchReleaseAsync(release.Domain, release.Id, ct);
        if (GetJsonString(result, "type") != "scene" && TryGetObject(result, "parent", out var parent) && GetJsonString(parent, "type") == "scene")
            result = parent;

        var scene = ToScene(result);
        return scene == null ? null : new ScrapedGalleryDto
        {
            Title = scene.Title,
            Code = scene.Code,
            Date = scene.Date,
            Details = scene.Details,
            ImageUrl = scene.ImageUrl,
            Urls = scene.Urls,
            StudioName = scene.StudioName,
            PerformerNames = scene.PerformerNames,
            TagNames = scene.TagNames,
        };
    }

    public async Task<ScrapedPerformerDto?> ScrapePerformerAsync(ScraperRequest<PerformerScrapeInput> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "performer"))
            return null;

        var url = FirstUrl(request.Input.Url, request.Input.Urls);
        if (!TryResolveActor(url, out var actor))
            return null;

        var result = await FetchApiResultAsync(actor.Domain, $"https://{ApiHost}/v1/actors/{Uri.EscapeDataString(actor.Id)}", ct);
        return ToPerformer(result, actor.Domain);
    }

    public async Task<IReadOnlyList<ScrapedPerformerDto>> SearchPerformersAsync(ScraperRequest<string> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "performer") || string.IsNullOrWhiteSpace(request.Input))
            return [];

        var results = new List<ScrapedPerformerDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in _domains)
        {
            var searchUrl = $"https://{ApiHost}/v1/actors?search={Uri.EscapeDataString(request.Input.Trim())}&limit=10";
            var json = await FetchApiResultAsync(domain, searchUrl, ct);
            if (json.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var item in json.EnumerateArray())
            {
                var name = GetJsonString(item, "name");
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    continue;

                var performer = ToPerformer(item, domain);
                if (performer != null)
                    results.Add(performer);
            }

            if (results.Count >= 10)
                break;
        }

        return results;
    }

    public async Task<ScrapedGroupDto?> ScrapeGroupAsync(ScraperRequest<GroupScrapeInput> request, CancellationToken ct)
    {
        if (!IsScraper(request.ScraperId, "group"))
            return null;

        var url = FirstUrl(request.Input.Url, request.Input.Urls);
        if (!TryResolveRelease(url, out var release))
            return null;

        var result = await FetchReleaseAsync(release.Domain, release.Id, ct);
        if (GetJsonString(result, "type") is "movie" or "serie")
            return ToGroup(result);

        if (TryGetObject(result, "parent", out var parent) && GetJsonString(parent, "type") is "movie" or "serie")
            return ToGroup(parent);

        return null;
    }

    protected virtual string? OverrideStudioName(string? studioName, JsonElement source)
        => _studioOverride ?? studioName;

    protected virtual string NormalizeUrl(string url, JsonElement source) => url;

    private async Task<JsonElement> FetchReleaseAsync(string domain, string id, CancellationToken ct)
        => await FetchApiResultAsync(domain, $"https://{ApiHost}/v2/releases/{Uri.EscapeDataString(id)}", ct);

    private async Task<JsonElement> FetchApiResultAsync(string domain, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Instance", await GetInstanceTokenAsync(domain, ct));
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:79.0) Gecko/20100101 Firefox/79.0");
        request.Headers.TryAddWithoutValidation("Origin", $"https://{domain}.com");
        request.Headers.Referrer = new Uri($"https://{domain}.com");

        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document.RootElement.Clone();

        if (document.RootElement.TryGetProperty("result", out var result))
            return result.Clone();

        return document.RootElement.Clone();
    }

    private async Task<string> GetInstanceTokenAsync(string domain, CancellationToken ct)
    {
        if (_tokens.TryGetValue(domain, out var token))
            return token;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://www.{domain}.com");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:79.0) Gecko/20100101 Firefox/79.0");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var tokenValue = ExtractInstanceToken(response);
        if (string.IsNullOrWhiteSpace(tokenValue))
            throw new InvalidOperationException($"Unable to get an Aylo API instance token for '{domain}'.");

        _tokens[domain] = tokenValue;
        return tokenValue;
    }

    private HttpClient GetHttpClient()
        => _services?.GetRequiredService<IHttpClientFactory>().CreateClient(Id)
            ?? throw new InvalidOperationException("The Aylo scraper has not been initialized.");

    private ScrapedSceneDto? ToScene(JsonElement scene)
    {
        if (GetJsonString(scene, "type") != "scene")
            return null;

        var sourceUrl = ConstructReleaseUrl(scene);
        return new ScrapedSceneDto
        {
            SourceScraperId = _sceneScraper.Id,
            Title = GetJsonString(scene, "title"),
            Code = GetJsonString(scene, "id"),
            Date = ParseDate(GetJsonString(scene, "dateReleased")),
            Details = CleanHtml(GetJsonString(scene, "description") ?? GetNestedString(scene, "parent", "description")),
            ImageUrl = NormalizeImageUrl(FindImageUrl(scene, ["poster", "poster_fallback"])),
            StudioName = OverrideStudioName(GetStudioName(scene), scene),
            PerformerNames = GetNamedArray(scene, "actors", "name"),
            TagNames = GetTagNames(scene),
            Urls = string.IsNullOrWhiteSpace(sourceUrl) ? [] : [NormalizeUrl(sourceUrl, scene)],
        };
    }

    private ScrapedPerformerDto? ToPerformer(JsonElement performer, string domain)
    {
        var name = GetJsonString(performer, "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var sourceUrl = ConstructPerformerUrl(performer, domain);
        return new ScrapedPerformerDto
        {
            SourceScraperId = _performerScraper.Id,
            Name = name,
            Gender = GetJsonString(performer, "gender"),
            Birthdate = ParseDate(GetJsonString(performer, "birthday")),
            Country = NormalizeCountry(GetJsonString(performer, "birthPlace")),
            HeightCm = ConvertInchesToCm(GetJsonInt(performer, "height")),
            Weight = ConvertPoundsToKg(GetJsonInt(performer, "weight")),
            Measurements = GetJsonString(performer, "measurements"),
            Details = CleanHtml(GetJsonString(performer, "bio")),
            ImageUrl = FindImageUrl(performer, ["master_profile"]),
            Aliases = GetStringArray(performer, "aliases").Where(alias => !string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)).ToList(),
            TagNames = GetTagNames(performer),
            Urls = string.IsNullOrWhiteSpace(sourceUrl) ? [] : [NormalizeUrl(sourceUrl, performer)],
        };
    }

    private ScrapedGroupDto? ToGroup(JsonElement group)
    {
        if (GetJsonString(group, "type") is not ("movie" or "serie"))
            return null;

        var sourceUrl = ConstructReleaseUrl(group);
        return new ScrapedGroupDto
        {
            SourceScraperId = _groupScraper.Id,
            Name = GetJsonString(group, "title"),
            Date = ParseDate(GetJsonString(group, "dateReleased")),
            Details = CleanHtml(GetJsonString(group, "description")),
            Synopsis = CleanHtml(GetJsonString(group, "description")),
            Duration = GetJsonInt(group, "durationSeconds"),
            Rating = GetJsonInt(group, "rating"),
            ImageUrl = NormalizeImageUrl(FindImageUrl(group, ["cover", "poster"])),
            Urls = string.IsNullOrWhiteSpace(sourceUrl) ? [] : [NormalizeUrl(sourceUrl, group)],
            StudioName = OverrideStudioName(GetStudioName(group), group),
            TagNames = GetTagNames(group),
        };
    }

    private bool IsScraper(string scraperId, string suffix)
        => string.Equals(scraperId, _extensionId + "/" + suffix, StringComparison.OrdinalIgnoreCase);

    private bool TryResolveRelease(string? url, out EntityLookup lookup)
        => TryResolveUrl(url, ["scene", "movie"], out lookup);

    private bool TryResolveActor(string? url, out EntityLookup lookup)
        => TryResolveUrl(url, ["model"], out lookup);

    private bool TryResolveUrl(string? url, IReadOnlyList<string> supportedSegments, out EntityLookup lookup)
    {
        lookup = default;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        var domain = ResolveDomain(uri.Host);
        if (domain == null)
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!supportedSegments.Contains(segments[i], StringComparer.OrdinalIgnoreCase))
                continue;

            var id = segments[i + 1];
            if (Regex.IsMatch(id, @"^\d+$"))
            {
                lookup = new EntityLookup(domain, id);
                return true;
            }
        }

        var fallback = Regex.Match(uri.AbsolutePath, @"/(\d+)(?:/|$)");
        if (fallback.Success)
        {
            lookup = new EntityLookup(domain, fallback.Groups[1].Value);
            return true;
        }

        return false;
    }

    private string? ResolveDomain(string host)
    {
        var normalized = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        foreach (var domain in _domains)
        {
            if (string.Equals(normalized, domain + ".com", StringComparison.OrdinalIgnoreCase))
                return domain;
        }

        return null;
    }

    private static string? FirstUrl(string? primary, IEnumerable<string> alternates)
        => !string.IsNullOrWhiteSpace(primary) ? primary : alternates.FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));

    private static string? ExtractInstanceToken(HttpResponseMessage response)
    {
        var headers = response.Headers.TryGetValues("Set-Cookie", out var responseCookies) ? responseCookies : [];
        var contentHeaders = response.Content.Headers.TryGetValues("Set-Cookie", out var contentCookies) ? contentCookies : [];
        foreach (var cookie in headers.Concat(contentHeaders))
        {
            var match = Regex.Match(cookie, @"(?:^|;\s*)instance_token=(?<token>[^;]+)", RegexOptions.IgnoreCase);
            if (match.Success)
                return match.Groups["token"].Value;
        }

        return null;
    }

    private string? ConstructReleaseUrl(JsonElement item)
    {
        var brand = GetJsonString(item, "brand");
        var type = GetJsonString(item, "type");
        var id = GetJsonString(item, "id");
        var title = GetJsonString(item, "title");
        if (string.IsNullOrWhiteSpace(brand) || brand == "leviproductions" || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        return $"https://www.{brand}.com/{type}/{id}/{Slugify(title)}";
    }

    private static string? ConstructPerformerUrl(JsonElement item, string domain)
    {
        var id = GetJsonString(item, "id");
        var name = GetJsonString(item, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return $"https://www.{domain}.com/model/{id}/{Slugify(name)}";
    }

    private string? GetStudioName(JsonElement item)
    {
        var studioName = GetNestedString(item, "collections", "0", "name");
        var parentName = GetNestedString(item, "brandMeta", "displayName")
            ?? GetNestedString(item, "brandMeta", "name")
            ?? GetNestedString(item, "brandMeta", "shortName");

        return studioName ?? parentName ?? _siteDisplayName;
    }

    private static List<string> GetTagNames(JsonElement item)
    {
        if (!item.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
            return [];

        return tags.EnumerateArray()
            .Select(tag =>
            {
                var id = GetJsonInt(tag, "id");
                if (id.HasValue && TagMap.TryGetValue(id.Value, out var mapped))
                    return mapped;

                return GetJsonString(tag, "name");
            })
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetNamedArray(JsonElement item, string arrayName, string propertyName)
    {
        if (!item.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(element => GetJsonString(element, propertyName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetStringArray(JsonElement item, string arrayName)
    {
        if (!item.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindImageUrl(JsonElement item, IReadOnlyList<string> imageKeys)
    {
        if (!item.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var imageKey in imageKeys)
        {
            if (!images.TryGetProperty(imageKey, out var imageGroup))
                continue;

            var url = FindImageUrlInGroup(imageGroup);
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return null;
    }

    private static string? FindImageUrlInGroup(JsonElement imageGroup)
    {
        if (imageGroup.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in imageGroup.EnumerateArray())
            {
                var url = FindImageUrlInGroup(item);
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }
        }

        if (imageGroup.ValueKind == JsonValueKind.Object)
        {
            foreach (var size in new[] { "xx", "xl", "lg", "md", "sm", "xs" })
            {
                if (imageGroup.TryGetProperty(size, out var value))
                {
                    var url = GetJsonString(value, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }

            var direct = GetJsonString(imageGroup, "url");
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            foreach (var property in imageGroup.EnumerateObject())
            {
                var nested = FindImageUrlInGroup(property.Value);
                if (!string.IsNullOrWhiteSpace(nested))
                    return nested;
            }
        }

        return null;
    }

    private static string? NormalizeImageUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : Regex.Replace(url, @"/m=[^/]+", string.Empty);

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

    private static int? ConvertInchesToCm(int? inches)
        => inches.HasValue && inches > 5 ? (int)Math.Round(inches.Value * 2.54) : null;

    private static int? ConvertPoundsToKg(int? pounds)
        => pounds.HasValue ? (int)Math.Round(pounds.Value / 2.205) : null;

    private static string? GetJsonString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return null;
    }

    private static int? GetJsonInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object)
            return true;

        value = default;
        return false;
    }

    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= current.GetArrayLength())
                    return null;
                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string Slugify(string value)
    {
        var noApostrophes = Regex.Replace(value, "['\u2019]", string.Empty);
        var slug = Regex.Replace(noApostrophes.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return Regex.Replace(slug, @"-{2,}", "-");
    }

    private static IReadOnlyList<string> BuildSupportedUrls(IReadOnlyList<string> domains, IReadOnlyList<string> pathSegments)
    {
        var urls = new List<string>();
        foreach (var domain in domains)
        {
            foreach (var segment in pathSegments)
            {
                urls.Add(domain + ".com/" + segment + "/*");
                urls.Add("www." + domain + ".com/" + segment + "/*");
            }
        }

        return urls;
    }

    private static IReadOnlyList<string> BuildPreferenceSites(IReadOnlyList<string> domains)
        => domains.Select(domain => domain + ".com").Append(ApiHost).ToList();

    private readonly record struct EntityLookup(string Domain, string Id);
}