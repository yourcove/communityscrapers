using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;

var options = SmokeOptions.Parse(args);
var linkRegex = new Regex("<a\\s+[^>]*href=[\"']([^\"'#]+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
};
var repoRoot = FindRepoRoot(AppContext.BaseDirectory);
var yamlRoot = Path.Combine(repoRoot, "extensions", "yaml");
var reportDirectory = options.ReportDirectory ?? Path.Combine(repoRoot, "artifacts", "verify", "yaml-smoke");
Directory.CreateDirectory(reportDirectory);

using var httpClient = new HttpClient(new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = true,
})
{
    Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
};
httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");

var extensionManager = new ExtensionManager(new ExtensionContext
{
    Configuration = new ConfigurationBuilder().Build(),
    DataDirectory = Path.Combine(Path.GetTempPath(), $"cove-yaml-smoke-{Guid.NewGuid():N}"),
    CoveVersion = "test",
});
extensionManager.DiscoverExtensions(yamlRoot);

var scraperService = new ScraperService(
    new CoveConfiguration(),
    NullLogger<ScraperService>.Instance,
    new SnapshotHttpClientFactory(httpClient, reportDirectory, options.FetchLive),
    extensionManager);

var allScrapers = scraperService.GetScrapers()
    .Where(scraper => scraper.Id.StartsWith("cove.community.scrapers.yaml.", StringComparison.OrdinalIgnoreCase))
    .OrderBy(scraper => scraper.Id, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (!string.IsNullOrWhiteSpace(options.Only))
{
    allScrapers = allScrapers
        .Where(scraper => scraper.Id.Contains(options.Only, StringComparison.OrdinalIgnoreCase)
            || scraper.Name.Contains(options.Only, StringComparison.OrdinalIgnoreCase))
        .ToList();
}

if (options.Skip > 0)
    allScrapers = allScrapers.Skip(options.Skip).ToList();

if (options.Limit > 0)
    allScrapers = allScrapers.Take(options.Limit).ToList();

var results = new List<ScraperSmokeResult>();
var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
Console.WriteLine($"YAML smoke runner: {allScrapers.Count} scraper registration(s), fetchLive={options.FetchLive}, timeout={options.TimeoutSeconds}s");

foreach (var scraper in allScrapers)
{
    if (!seenIds.Add(scraper.Id)) continue;

    var stopwatch = Stopwatch.StartNew();
    var result = await RunScraperAsync(scraper, scraperService, httpClient, options, stopwatch);
    stopwatch.Stop();

    results.Add(result);
    Console.WriteLine($"{result.Status,-18} {result.ScraperId} {result.SampleUrl ?? string.Empty} {result.Message ?? string.Empty}");
}

var report = new SmokeReport(
    GeneratedAt: DateTimeOffset.UtcNow,
    FetchLive: options.FetchLive,
    TimeoutSeconds: options.TimeoutSeconds,
    Total: results.Count,
    Counts: results.GroupBy(result => result.Status).OrderBy(group => group.Key).ToDictionary(group => group.Key, group => group.Count()),
    Results: results);

var reportPath = Path.Combine(reportDirectory, "yaml-smoke-report.json");
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, jsonOptions) + Environment.NewLine);

var markdownPath = Path.Combine(reportDirectory, "yaml-smoke-report.md");
await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report));

Console.WriteLine();
Console.WriteLine($"Report: {reportPath}");
Console.WriteLine($"Summary: {string.Join(", ", report.Counts.Select(kv => $"{kv.Key}={kv.Value}"))}");

return report.Counts.TryGetValue(SmokeStatus.EngineError, out var engineErrors) && engineErrors > 0 ? 2 : 0;

async Task<ScraperSmokeResult> RunScraperAsync(
    ScraperSummaryDto scraper,
    ScraperService service,
    HttpClient client,
    SmokeOptions options,
    Stopwatch stopwatch)
{
    var sampleUrl = await ResolveSampleUrlAsync(scraper, client, options);
    if (sampleUrl == null)
    {
        return CreateResult(scraper, SmokeStatus.NoSampleUrl, null, stopwatch, null, "Could not derive or discover a sample URL.", null);
    }

    if (!options.FetchLive)
    {
        return CreateResult(scraper, SmokeStatus.NotRunDryRun, sampleUrl, stopwatch, null, "Dry run only; rerun with --live to fetch and scrape.", null);
    }

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var scrapeResult = await service.ScrapeUrlAsync(scraper.Id, scraper.EntityType, sampleUrl, timeout.Token);
        if (scrapeResult == null || scrapeResult.Count == 0)
        {
            return CreateResult(scraper, SmokeStatus.EmptyResult, sampleUrl, stopwatch, scrapeResult, "Scrape returned no fields.", null);
        }

        var populated = scrapeResult
            .Where(kv => HasValue(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (populated.Count == 0)
        {
            return CreateResult(scraper, SmokeStatus.EmptyResult, sampleUrl, stopwatch, scrapeResult, "Scrape fields were present but empty.", null);
        }

        if (!HasPrimaryField(scraper.EntityType, scrapeResult))
        {
            return CreateResult(scraper, SmokeStatus.WeakResult, sampleUrl, stopwatch, scrapeResult, $"Missing expected primary field for {scraper.EntityType}. Populated: {string.Join(", ", populated.Take(8))}", null);
        }

        return CreateResult(scraper, SmokeStatus.Pass, sampleUrl, stopwatch, scrapeResult, $"Populated: {string.Join(", ", populated.Take(8))}", null);
    }
    catch (OperationCanceledException ex)
    {
        return CreateResult(scraper, SmokeStatus.Timeout, sampleUrl, stopwatch, null, ex.Message, ex.GetType().FullName);
    }
    catch (HttpRequestException ex)
    {
        return CreateResult(scraper, SmokeStatus.NetworkError, sampleUrl, stopwatch, null, ex.Message, ex.GetType().FullName);
    }
    catch (Exception ex)
    {
        if (ContainsException<OperationCanceledException>(ex))
        {
            return CreateResult(scraper, SmokeStatus.Timeout, sampleUrl, stopwatch, null, ex.Message, ex.GetType().FullName);
        }

        if (ContainsException<HttpRequestException>(ex))
        {
            return CreateResult(scraper, SmokeStatus.NetworkError, sampleUrl, stopwatch, null, ex.Message, ex.GetType().FullName);
        }

        return CreateResult(scraper, SmokeStatus.EngineError, sampleUrl, stopwatch, null, ex.ToString(), ex.GetType().FullName);
    }
}

static bool ContainsException<TException>(Exception exception)
    where TException : Exception
{
    for (var current = exception; current != null; current = current.InnerException)
    {
        if (current is TException) return true;
    }

    return false;
}

async Task<string?> ResolveSampleUrlAsync(ScraperSummaryDto scraper, HttpClient client, SmokeOptions options)
{
    var explicitUrl = options.SampleUrls.TryGetValue(scraper.Id, out var byId)
        ? byId
        : options.SampleUrls.TryGetValue(GetExtensionId(scraper.Id), out var byExtension)
            ? byExtension
            : null;

    if (!string.IsNullOrWhiteSpace(explicitUrl))
        return explicitUrl;

    var candidates = scraper.Urls
        .SelectMany(NormalizePatternToCandidateUrls)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (!options.FetchLive)
        return candidates.FirstOrDefault();

    foreach (var candidate in candidates.Take(6))
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var discovered = await DiscoverCandidateUrlAsync(candidate, scraper, client, timeout.Token);
            if (!string.IsNullOrWhiteSpace(discovered))
                return discovered;
        }
        catch
        {
            // Try the next candidate host; the scrape attempt will record final failures.
        }
    }

    return candidates.FirstOrDefault();
}

async Task<string?> DiscoverCandidateUrlAsync(string baseUrl, ScraperSummaryDto scraper, HttpClient client, CancellationToken ct)
{
    using var response = await client.GetAsync(baseUrl, ct);
    if (!response.IsSuccessStatusCode) return baseUrl;

    var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
    if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)) return baseUrl;

    var html = await response.Content.ReadAsStringAsync(ct);
    var baseUri = response.RequestMessage?.RequestUri ?? new Uri(baseUrl);

    var links = linkRegex.Matches(html)
        .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
        .Select(link => ToAbsoluteUrl(baseUri, link))
        .Where(link => link != null)
        .Select(link => link!)
        .Where(link => IsSameHost(baseUri, link))
        .Where(link => !IsDisallowedSampleUrl(link))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    var specificSupportedPatterns = scraper.Urls
        .Select(NormalizeSpecificSupportedPattern)
        .Where(pattern => pattern != null)
        .Select(pattern => pattern!)
        .ToList();

    return links
        .Select(link => new { Url = link, Score = ScoreCandidateLink(link, scraper.EntityType, specificSupportedPatterns) })
        .Where(candidate => candidate.Score > 0)
        .OrderByDescending(candidate => candidate.Score)
        .ThenByDescending(candidate => candidate.Url.Length)
        .Select(candidate => candidate.Url)
        .FirstOrDefault()
        ?? (LooksLikeEntityDetailUrl(baseUrl, scraper.EntityType) ? baseUrl : null);
}

static ScraperSmokeResult CreateResult(
    ScraperSummaryDto scraper,
    string status,
    string? sampleUrl,
    Stopwatch stopwatch,
    Dictionary<string, object>? fields,
    string? message,
    string? exceptionType)
{
    return new ScraperSmokeResult(
        ScraperId: scraper.Id,
        Name: scraper.Name,
        EntityType: scraper.EntityType,
        Status: status,
        SampleUrl: sampleUrl,
        ElapsedMilliseconds: stopwatch.ElapsedMilliseconds,
        FieldNames: fields?.Where(kv => HasValue(kv.Value)).Select(kv => kv.Key).OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToList() ?? [],
        Message: message,
        ExceptionType: exceptionType);
}

static bool HasValue(object? value)
{
    if (value == null) return false;
    if (value is string text) return !string.IsNullOrWhiteSpace(text);
    if (value is System.Collections.IEnumerable enumerable)
    {
        foreach (var item in enumerable)
        {
            if (item != null) return true;
        }
        return false;
    }
    return true;
}

static bool HasPrimaryField(string entityType, IReadOnlyDictionary<string, object> fields)
{
    string[] primaryFields = entityType.ToLowerInvariant() switch
    {
        "scene" => ["Title"],
        "performer" => ["Name"],
        "gallery" => ["Title", "URL"],
        "image" => ["Title", "URL"],
        "group" or "movie" => ["Name", "Title"],
        "audio" => ["Title"],
        "text" => ["Title"],
        _ => ["Title", "Name"],
    };

    return primaryFields.Any(field => fields.TryGetValue(field, out var value) && HasValue(value));
}

static IEnumerable<string> NormalizePatternToCandidateUrls(string pattern)
{
    if (string.IsNullOrWhiteSpace(pattern)) yield break;

    var value = pattern.Trim().Trim('/');
    if (value.Length == 0) yield break;

    var scheme = value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://" : "https://";
    value = value.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("*.", "www.", StringComparison.OrdinalIgnoreCase)
        .Replace("*", string.Empty, StringComparison.OrdinalIgnoreCase);

    var host = value.Split('/', '?', '#')[0].Trim('.');
    if (!host.Contains('.', StringComparison.Ordinal)) yield break;

    var root = scheme + host + "/";
    var slash = value.IndexOf('/');
    if (slash >= 0 && slash < value.Length - 1)
    {
        var path = value[slash..].Trim();
        if (path.Length > 1 && !path.Contains('{', StringComparison.Ordinal))
            yield return scheme + host + path;
    }

    yield return root;
}

static string? NormalizeSpecificSupportedPattern(string pattern)
{
    if (string.IsNullOrWhiteSpace(pattern)) return null;

    var value = pattern.Trim().Trim('/');
    if (value.Length == 0) return null;

    value = value.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("*.", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("*", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Trim('.');

    var slash = value.IndexOf('/');
    if (slash < 0 || slash == value.Length - 1) return null;

    var path = value[slash..];
    return path.Length > 1 ? value : null;
}

static int ScoreCandidateLink(string url, string entityType, IReadOnlyList<string> specificSupportedPatterns)
{
    if (IsDisallowedSampleUrl(url)) return 0;

    var score = 0;
    if (specificSupportedPatterns.Any(pattern => UrlContainsPattern(url, pattern))) score += 100;
    if (LooksLikeContentUrl(url)) score += 40;
    if (LooksLikeEntityDetailUrl(url, entityType)) score += 80;

    return score;
}

static bool UrlContainsPattern(string url, string pattern)
{
    var comparable = url.Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
        .TrimEnd('/');
    return comparable.Contains(pattern.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

static bool IsDisallowedSampleUrl(string url)
{
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return true;

    var path = uri.AbsolutePath.ToLowerInvariant();
    if (path is "/" or "") return false;
    if (path is "/members" or "/members/") return true;

    return Regex.IsMatch(path, @"/(help|terms|privacy|legal|dmca|2257|contact|support|faq|login|signup|join|billing|cart|account|password|affiliate)(/|_|-|\.|$)", RegexOptions.IgnoreCase);
}

static bool LooksLikeContentUrl(string url)
{
    if (IsDisallowedSampleUrl(url) ||
        url.Contains("/tag", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/category", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    return Regex.IsMatch(url, @"/(video|scene|movie|episode|clip|play|watch|view|dvd|trailers?|updates?|gallery|photos?|sets?|models?|profile|performer|pornstar|girls?)/", RegexOptions.IgnoreCase)
        || Regex.IsMatch(url, @"/[A-Za-z0-9_-]+-\d+/?$", RegexOptions.IgnoreCase)
        || Regex.IsMatch(url, @"/\d{3,}/?$");
}

static bool LooksLikeEntityDetailUrl(string url, string entityType)
{
    var sceneLike = Regex.IsMatch(url, @"/(video|scene|movie|episode|clip|play|watch|view|dvd|trailers?|updates?)/[^/?#]+", RegexOptions.IgnoreCase)
        || Regex.IsMatch(url, @"/\d{3,}/?$", RegexOptions.IgnoreCase);
    var performerLike = Regex.IsMatch(url, @"/(models?|profile|performer|pornstar|girls?|star)/[^/?#]+", RegexOptions.IgnoreCase);
    var galleryLike = Regex.IsMatch(url, @"/(gallery|photos?|sets?)/[^/?#]+", RegexOptions.IgnoreCase);

    return entityType.ToLowerInvariant() switch
    {
        "performer" => performerLike,
        "gallery" or "image" => galleryLike,
        "group" or "movie" => sceneLike,
        _ => sceneLike,
    };
}

static string? ToAbsoluteUrl(Uri baseUri, string href)
{
    if (string.IsNullOrWhiteSpace(href)) return null;
    if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
    return Uri.TryCreate(baseUri, href, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        ? uri.ToString()
        : null;
}

static bool IsSameHost(Uri baseUri, string url)
    => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && string.Equals(NormalizeHost(baseUri.Host), NormalizeHost(uri.Host), StringComparison.OrdinalIgnoreCase);

static string NormalizeHost(string host)
    => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

static string GetExtensionId(string scraperId)
{
    var slash = scraperId.IndexOf('/');
    return slash >= 0 ? scraperId[..slash] : scraperId;
}

static string FindRepoRoot(string startPath)
{
    var dir = new DirectoryInfo(startPath);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "package.json")) && Directory.Exists(Path.Combine(dir.FullName, "extensions", "yaml")))
            return dir.FullName;
        dir = dir.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate communityscrapers repo root.");
}

static string BuildMarkdown(SmokeReport report)
{
    var writer = new StringWriter();
    writer.WriteLine("# YAML Scraper Smoke Report");
    writer.WriteLine();
    writer.WriteLine($"Generated: {report.GeneratedAt:u}");
    writer.WriteLine($"Fetch live: {report.FetchLive}");
    writer.WriteLine($"Total: {report.Total}");
    writer.WriteLine();
    writer.WriteLine("## Counts");
    writer.WriteLine();
    foreach (var (status, count) in report.Counts.OrderBy(kv => kv.Key))
        writer.WriteLine($"- {status}: {count}");
    writer.WriteLine();
    writer.WriteLine("## Results");
    writer.WriteLine();
    writer.WriteLine("| Status | Scraper | URL | Message |");
    writer.WriteLine("| --- | --- | --- | --- |");
    foreach (var result in report.Results.OrderBy(result => result.Status).ThenBy(result => result.ScraperId, StringComparer.OrdinalIgnoreCase))
    {
        writer.WriteLine($"| {EscapeMarkdown(result.Status)} | {EscapeMarkdown(result.ScraperId)} | {EscapeMarkdown(result.SampleUrl ?? string.Empty)} | {EscapeMarkdown((result.Message ?? string.Empty).Split('\n')[0])} |");
    }
    return writer.ToString();
}

static string EscapeMarkdown(string value)
    => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

internal static class SmokeStatus
{
    public const string Pass = "pass";
    public const string WeakResult = "weak-result";
    public const string EmptyResult = "empty-result";
    public const string EngineError = "engine-error";
    public const string NetworkError = "network-error";
    public const string Timeout = "timeout";
    public const string NoSampleUrl = "no-sample-url";
    public const string NotRunDryRun = "not-run-dry-run";
}

internal sealed record ScraperSmokeResult(
    string ScraperId,
    string Name,
    string EntityType,
    string Status,
    string? SampleUrl,
    long ElapsedMilliseconds,
    List<string> FieldNames,
    string? Message,
    string? ExceptionType);

internal sealed record SmokeReport(
    DateTimeOffset GeneratedAt,
    bool FetchLive,
    int TimeoutSeconds,
    int Total,
    Dictionary<string, int> Counts,
    List<ScraperSmokeResult> Results);

internal sealed record SmokeOptions
{
    public bool FetchLive { get; private init; }
    public int Skip { get; private init; }
    public int Limit { get; private init; }
    public string? Only { get; private init; }
    public int TimeoutSeconds { get; private init; } = 20;
    public string? ReportDirectory { get; private init; }
    public Dictionary<string, string> SampleUrls { get; private init; } = new(StringComparer.OrdinalIgnoreCase);

    public static SmokeOptions Parse(string[] args)
    {
        var options = new SmokeOptions();
        var samplesPath = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--live":
                    options = options with { FetchLive = true };
                    break;
                case "--limit":
                    options = options with { Limit = int.Parse(args[++i]) };
                    break;
                case "--skip":
                    options = options with { Skip = int.Parse(args[++i]) };
                    break;
                case "--only":
                    options = options with { Only = args[++i] };
                    break;
                case "--timeout":
                    options = options with { TimeoutSeconds = int.Parse(args[++i]) };
                    break;
                case "--report-dir":
                    options = options with { ReportDirectory = Path.GetFullPath(args[++i]) };
                    break;
                case "--samples":
                    samplesPath = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (!string.IsNullOrWhiteSpace(samplesPath))
        {
            if (!File.Exists(samplesPath))
                throw new FileNotFoundException("Sample URL file not found.", samplesPath);

            var samples = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(samplesPath))
                ?? new Dictionary<string, string>();

            options = options with
            {
                SampleUrls = new Dictionary<string, string>(samples, StringComparer.OrdinalIgnoreCase),
            };
        }

        return options;
    }
}

internal sealed class SnapshotHttpClientFactory(HttpClient liveClient, string reportDirectory, bool fetchLive) : IHttpClientFactory
{
    private readonly ConcurrentDictionary<string, byte> _saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _snapshotDirectory = Path.Combine(reportDirectory, "snapshots");

    public HttpClient CreateClient(string name)
        => new(new SnapshotHttpMessageHandler(liveClient, _snapshotDirectory, _saved, fetchLive));
}

internal sealed class SnapshotHttpMessageHandler(HttpClient liveClient, string snapshotDirectory, ConcurrentDictionary<string, byte> saved, bool fetchLive) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!fetchLive)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            };
        }

        using var upstreamRequest = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);

        var upstream = await liveClient.SendAsync(upstreamRequest, cancellationToken);
        var bytes = await upstream.Content.ReadAsByteArrayAsync(cancellationToken);
        SaveSnapshot(request.RequestUri?.ToString() ?? string.Empty, bytes);

        var response = new HttpResponseMessage(upstream.StatusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
            ReasonPhrase = upstream.ReasonPhrase,
        };
        foreach (var header in upstream.Headers)
            response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var header in upstream.Content.Headers)
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return response;
    }

    private void SaveSnapshot(string url, byte[] bytes)
    {
        if (bytes.Length == 0 || !saved.TryAdd(url, 0)) return;
        Directory.CreateDirectory(snapshotDirectory);
        var fileName = Regex.Replace(url, @"[^A-Za-z0-9._-]+", "_");
        if (fileName.Length > 140) fileName = fileName[..140];
        File.WriteAllBytes(Path.Combine(snapshotDirectory, fileName + ".bin"), bytes);
    }
}
