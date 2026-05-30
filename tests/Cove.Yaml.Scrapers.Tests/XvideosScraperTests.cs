using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Plugins;

namespace Cove.Yaml.Scrapers.Tests;

/// <summary>
/// End-to-end proof that a source-served YAML scraper pack in this repo
/// (extensions/yaml/xvideos) is discovered and executed by Cove's real
/// ScraperService engine, including the Go reference-layout parseDate fix.
/// </summary>
public class XvideosScraperTests
{
    private const string SceneUrl = "https://www.xvideos.com/video.abcdef/test_scene";

    private static readonly string SceneHtml = """
        <html>
          <head>
            <link rel="alternate" hreflang="x-default" href="https://www.xvideos.com/video.abcdef/test_scene" />
            <script type="application/ld+json">
              {
                "@context": "https://schema.org",
                "@type": "VideoObject",
                "name": "Test Scene Title",
                "uploadDate": "2024-05-10T08:30:00+00:00"
              }
            </script>
          </head>
          <body>
            <h2 class="page-title">Test Scene Title<span class="duration">10 min</span></h2>
            <div class="video-tags-list"><a href="/tags/example">Example</a></div>
            <li class="model"><span class="name">Jane Doe</span></li>
          </body>
        </html>
        """;

    [Fact]
    public void XvideosPack_IsDiscovered_AndAppearsInScraperList()
    {
        var service = CreateService();

        var scrapers = service.GetScrapers();

        Assert.Contains(scrapers, s => s.Name.Equals("Xvideos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void XvideosPack_MatchesSceneUrl()
    {
        var service = CreateService();

        var matches = service.FindScrapersForUrl(SceneUrl, "scene");

        Assert.Contains(matches, m => m.Name.Equals("Xvideos", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task XvideosPack_ScrapesTitleAndParsesGoLayoutDate()
    {
        var service = CreateService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SceneUrl] = SceneHtml,
        });

        var result = await service.ScrapeUrlAutoAsync(SceneUrl, "scene");

        Assert.NotNull(result);
        Assert.Equal("Test Scene Title", GetString(result!.Value.Result, "Title"));
        // The scraper's Date field uses `parseDate: 2006-01-02` (a Go reference
        // layout). This asserts Cove's ConvertGoLayoutToNetFormat fix turns the
        // ld+json uploadDate into an ISO date rather than failing to parse.
        Assert.Equal("2024-05-10", GetString(result.Value.Result, "Date"));
    }

    private static string? GetString(Dictionary<string, object>? result, string key)
        => result is not null && result.TryGetValue(key, out var value) ? value as string : null;

    private static ScraperService CreateService(IReadOnlyDictionary<string, string>? responses = null)
    {
        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.Combine(Path.GetTempPath(), $"cove-yaml-tests-{Guid.NewGuid():N}"),
            CoveVersion = "test",
        });

        extensionManager.DiscoverExtensions(LocateYamlExtensionsDir());

        return new ScraperService(
            new CoveConfiguration(),
            NullLogger<ScraperService>.Instance,
            new FakeHttpClientFactory(responses ?? new Dictionary<string, string>()),
            extensionManager);
    }

    /// <summary>Walks up from the test binary to find this repo's extensions/yaml folder.</summary>
    private static string LocateYamlExtensionsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "extensions", "yaml");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "xvideos")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate extensions/yaml. Run tests from within the communityscrapers repo.");
    }

    private sealed class FakeHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FakeHttpMessageHandler(responses))
            {
                BaseAddress = new Uri("https://www.xvideos.com/"),
            };
    }

    private sealed class FakeHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (!responses.TryGetValue(url, out var html))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = request,
            });
        }
    }
}
