// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/Erotik/Erotik.yml; scrapers/Erotik/Erotik.py
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cove.Core.DTOs;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Extensions.CommunityScrapers;

public sealed class ErotikScraperExtension : IScraperProvider
{
    private const string ExtensionId = "cove.community.scrapers.erotik";
    private const string GroupScraperId = "cove.community.scrapers.erotik/group";

    private static readonly ScraperDescriptor GroupScraper = new(
        GroupScraperId,
        "Erotik Group",
        ScraperEntity.Group,
        ScraperCapabilities.ByUrl,
        ["erotik.com/*", "*.erotik.com/*"],
        ScraperRiskLevel.NetworkOnly,
        ["erotik.com", "api.erotik.com"]);

    private IServiceProvider? _services;

    public string Id => ExtensionId;
    public string Name => "Erotik Scraper";
    public string Version => OfficialDownloaderUtilities.GetExtensionVersion(typeof(ErotikScraperExtension));
    public string? Description => "Extracts group metadata from Erotik pages.";
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

    public IReadOnlyList<ScraperDescriptor> GetScrapers() => [GroupScraper];

    public async Task<ScrapedGroupDto?> ScrapeGroupAsync(ScraperRequest<GroupScrapeInput> request, CancellationToken ct)
    {
        if (!string.Equals(request.ScraperId, GroupScraperId, StringComparison.OrdinalIgnoreCase))
            return null;

        var url = request.Input.Url ?? request.Input.Urls.FirstOrDefault(IsErotikUrl);
        if (string.IsNullOrWhiteSpace(url) || !IsErotikUrl(url))
            return null;

        var html = await GetStringAsync(url, ct);
        var groupId = ExtractMetaContent(html, "id");
        if (string.IsNullOrWhiteSpace(groupId))
            return null;

        var apiJson = await GetStringAsync($"https://api.erotik.com/content/movies?idList[]={Uri.EscapeDataString(groupId)}", ct);
        using var document = JsonDocument.Parse(apiJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            return null;

        return ParseGroup(document.RootElement[0]);
    }

    private static ScrapedGroupDto ParseGroup(JsonElement item)
    {
        return new ScrapedGroupDto
        {
            SourceScraperId = GroupScraperId,
            Name = GetLocalizedString(item, "title"),
            Synopsis = CleanHtml(GetLocalizedString(item, "description")),
            Details = CleanHtml(GetLocalizedString(item, "description")),
            StudioName = GetNestedString(item, "studio", "name"),
            ImageUrl = GetNestedString(item, "image", "default"),
            Director = JoinNames(item, "directors"),
            TagNames = GetCategoryNames(item),
            Duration = GetJsonInt(item, "durationSeconds"),
            Rating = GetJsonInt(item, "rating"),
            Date = GetJsonString(item, "releaseYear"),
            Urls = GetObjectStringValues(item, "url"),
        };
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CoveErotikScraper/1.0");
        using var response = await GetHttpClient().SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private HttpClient GetHttpClient()
        => _services?.GetRequiredService<IHttpClientFactory>().CreateClient(Id)
            ?? throw new InvalidOperationException("The Erotik scraper has not been initialized.");

    private static bool IsErotikUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && OfficialDownloaderUtilities.IsHost(uri, "erotik.com");
    }

    private static string? ExtractMetaContent(string html, string name)
    {
        foreach (Match match in Regex.Matches(html, @"(?is)<meta\b(?<attrs>[^>]+)>"))
        {
            var attrs = match.Groups["attrs"].Value;
            if (!Regex.IsMatch(attrs, $@"(?i)\bname\s*=\s*(['""']){Regex.Escape(name)}\1"))
                continue;

            var content = ExtractAttribute(attrs, "content");
            if (!string.IsNullOrWhiteSpace(content))
                return WebUtility.HtmlDecode(content).Trim();
        }

        return null;
    }

    private static string? ExtractAttribute(string text, string attributeName)
    {
        var match = Regex.Match(text, $@"(?i)\b{Regex.Escape(attributeName)}\s*=\s*(['""'])(?<value>.*?)\1");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? GetLocalizedString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.String)
            return value.GetString();

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("en", out var english) && english.ValueKind == JsonValueKind.String)
                return english.GetString();

            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? GetNestedString(JsonElement item, params string[] path)
    {
        var current = item;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.ToString();
    }

    private static string? GetJsonString(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetJsonInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
            return parsed;

        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : null;
    }

    private static string? JoinNames(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        var names = array.EnumerateArray()
            .Select(element => GetJsonString(element, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    private static List<string> GetCategoryNames(JsonElement item)
    {
        if (!item.TryGetProperty("categories", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(category => (GetJsonInt(category, "sortOrder") ?? -1) >= 0)
            .Select(category => GetLocalizedString(category, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetObjectStringValues(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
            return [];

        return value.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.String)
            .Select(property => property.Value.GetString())
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
}