using System.Net;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Extensions.CommunityScrapers;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Yaml.Scrapers.Tests;

public sealed class FakNetworkScraperTests
{
    private const string SceneUrl = "https://fakings.com/en/video/fixture-slug";
    private const string SceneApiUrl = "https://api.faknetworks.com/v1/public/videos/fixture-slug?lang=en";
    private const string CategoriesApiUrl = "https://api.faknetworks.com/v1/categories?product=fakings&lang=en&page=1&take=1000";
    private const string SearchApiUrl = "https://api.faknetworks.com/v1/search?query=fixture&product=fakings&lang=en&limit=10";

    private static readonly string SceneJson = JsonSerializer.Serialize(new
    {
        product = "fakings",
        title = "Fixture Scene Title",
        date = "2025-05-10T00:00:00Z",
        filename = "fixture-code",
        slug = "fixture-slug",
        description = "<p>Fixture <strong>details</strong>.</p>",
        horizontalProfile = "fixture-image.jpg",
        serie = new { id = 215, title = "Fixture Series" },
        categories = new[] { new { id = 12, title = "Fallback Tag" } },
        performers = new[] { new { name = "Fixture Performer", product = "fakings", slug = "fixture-performer", profilePhoto = "performer.jpg" } },
    });

    private static readonly string CategoriesJson = JsonSerializer.Serialize(new
    {
        results = new[] { new { id = 12, title = "Mapped Tag" } },
    });

    [Fact]
    public async Task ScrapeVideoAsync_ParsesFakNetworkVideoMetadata()
    {
        var extension = CreateExtension(new FaKingsScraperExtension(), new Dictionary<string, string>
        {
            [SceneApiUrl] = SceneJson,
            [CategoriesApiUrl] = CategoriesJson,
        });

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                "cove.community.scrapers.fakings/video",
                new VideoScrapeInput { Url = SceneUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Scene Title", result!.Title);
        Assert.Equal("fixture-code", result.Code);
        Assert.Equal("2025-05-10", result.Date);
        Assert.Equal("Fixture details.", result.Details);
        Assert.Equal("Fixture Series", result.StudioName);
        Assert.Equal("https://player.faknetworks.com/almacen/videos/listado_horizontal_fixture-image.jpg", result.ImageUrl);
        Assert.Equal("https://fakings.com/en/video/fixture-slug", Assert.Single(result.Urls));
        Assert.Contains("Fixture Performer", result.PerformerNames);
        Assert.Contains("Mapped Tag", result.TagNames);
    }

    [Fact]
    public async Task SearchVideosAsync_UsesFakNetworkSearchApi()
    {
        var extension = CreateExtension(new FaKingsScraperExtension(), new Dictionary<string, string>
        {
            [SearchApiUrl] = JsonSerializer.Serialize(new { videos = new[] { JsonSerializer.Deserialize<JsonElement>(SceneJson) } }),
            [CategoriesApiUrl] = CategoriesJson,
        });

        var results = await extension.SearchVideosAsync(
            new ScraperRequest<string>("cove.community.scrapers.fakings/video", "fixture", new ScraperPermissions()),
            CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Fixture Scene Title", result.Title);
        Assert.Equal("fixture-code", result.Code);
    }

    [Fact]
    public void FakNetworkPorts_AdvertiseExpectedVideoScrapers()
    {
        AssertVideoScraper(new FaKingsScraperExtension(), "cove.community.scrapers.fakings/video", "FaKings Video");
        AssertVideoScraper(new MadLifesScraperExtension(), "cove.community.scrapers.madlifes/video", "MadLifes Video");
        AssertVideoScraper(new PepePornScraperExtension(), "cove.community.scrapers.pepeporn/video", "PepePorn Video");
    }

    private static void AssertVideoScraper(IScraperProvider extension, string scraperId, string scraperName)
    {
        var scraper = Assert.Single(extension.GetScrapers());
        Assert.Equal(scraperId, scraper.Id);
        Assert.Equal(scraperName, scraper.Name);
        Assert.Equal(ScraperEntity.Video, scraper.Entity);
        Assert.True(scraper.Capabilities.HasFlag(ScraperCapabilities.ByUrl));
        Assert.True(scraper.Capabilities.HasFlag(ScraperCapabilities.ByName));
    }

    private static TExtension CreateExtension<TExtension>(TExtension extension, IReadOnlyDictionary<string, string> responses)
        where TExtension : IScraperProvider
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(responses))
            .BuildServiceProvider();

        extension.InitializeAsync(services).GetAwaiter().GetResult();
        return extension;
    }

    private sealed class FakeHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FakeHttpMessageHandler(responses));
    }

    private sealed class FakeHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (responses.TryGetValue(url, out var content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content),
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }
    }
}