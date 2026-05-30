using System.Net;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Extensions.CommunityScrapers;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Yaml.Scrapers.Tests;

public sealed class PornhubScraperTests
{
    private const string SceneUrl = "https://www.pornhub.com/view_video.php?viewkey=ph12345678901";
    private const string PerformerUrl = "https://www.pornhub.com/model/example-model";

    private static readonly string SceneHtml = """
        <html>
          <head>
            <meta property="og:url" content="https://www.pornhub.com/view_video.php?viewkey=ph12345678901" />
            <meta property="og:image" content="https://ei.example.invalid/thumb.jpg" />
          </head>
          <body>
            <script type="text/javascript">
              window.dataLayer.push({'video_date_published':'20240605'});
            </script>
            <h1>Fixture Scene Title</h1>
            <div class="video-detailed-info">
              <a data-label="category" href="/video?c=1">Category One</a>
              <a data-label="tag" href="/video?o=1">Tag One</a>
              <a data-label="pornstar" href="/model/example-model">Example Model</a>
              <a data-label="channel" href="/channels/example-channel">Example Channel</a>
            </div>
          </body>
        </html>
        """;

    private static readonly string PerformerHtml = """
        <html>
          <head>
            <link rel="canonical" href="https://www.pornhub.com/model/example-model" />
          </head>
          <body>
            <h1 itemprop="name">Example Model</h1>
            <span itemprop="birthDate">1990-02-03</span>
            <div class="infoPiece"><span>Birthplace:</span><span class="smallInfo">Example City, US</span></div>
            <div class="infoPiece"><span>Gender:</span><span class="smallInfo">Trans Woman</span></div>
            <div class="infoPiece"><span>Height:</span><span class="smallInfo">5 ft 7 in (170 cm)</span></div>
            <div class="infoPiece"><span>Weight:</span><span class="smallInfo">130 lbs (59 kg)</span></div>
            <div class="infoPiece"><span>Measurements:</span><span class="smallInfo">34-24-34</span></div>
            <div class="infoPiece"><span>Hair Color:</span><span class="smallInfo">Black</span></div>
            <div class="infoPiece"><span>Tattoos:</span><span class="smallInfo">None</span></div>
            <div itemprop="description">Fixture performer biography.</div>
            <div class="thumbImage"><img src="https://ei.example.invalid/model.jpg" /></div>
          </body>
        </html>
        """;

    [Fact]
    public void PornhubScraper_AdvertisesSceneAndPerformerScrapers()
    {
        var extension = CreateExtension();

        var scrapers = extension.GetScrapers();

        Assert.Contains(scrapers, scraper => scraper.Id == "cove.community.scrapers.pornhub/scene");
        Assert.Contains(scrapers, scraper => scraper.Id == "cove.community.scrapers.pornhub/performer");
    }

    [Fact]
    public async Task ScrapeSceneAsync_ParsesSceneMetadata()
    {
        var extension = CreateExtension(new Dictionary<string, string>
        {
            [SceneUrl] = SceneHtml,
        });

        var result = await extension.ScrapeSceneAsync(
            new ScraperRequest<SceneScrapeInput>(
                "cove.community.scrapers.pornhub/scene",
                new SceneScrapeInput { Url = SceneUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Scene Title", result!.Title);
        Assert.Equal("ph12345678901", result.Code);
        Assert.Equal("2024-06-05", result.Date);
        Assert.Equal("https://ei.example.invalid/thumb.jpg", result.ImageUrl);
        Assert.Equal("Example Channel", result.StudioName);
        Assert.Contains("Example Model", result.PerformerNames);
        Assert.Contains("Category One", result.TagNames);
        Assert.Contains("Tag One", result.TagNames);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ParsesPerformerMetadata()
    {
        var extension = CreateExtension(new Dictionary<string, string>
        {
            [PerformerUrl] = PerformerHtml,
        });

        var result = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                "cove.community.scrapers.pornhub/performer",
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Example Model", result!.Name);
        Assert.Equal("1990-02-03", result.Birthdate);
        Assert.Equal("USA", result.Country);
        Assert.Equal("transgender_female", result.Gender);
        Assert.Equal(170, result.HeightCm);
        Assert.Equal(59, result.Weight);
        Assert.Equal("34-24-34", result.Measurements);
        Assert.Equal("Black", result.HairColor);
        Assert.Equal("None", result.Tattoos);
        Assert.Equal("Fixture performer biography.", result.Details);
        Assert.Equal("https://ei.example.invalid/model.jpg", result.ImageUrl);
    }

    [Fact]
    public async Task SearchPerformersAsync_UsesAutocompleteApi()
    {
        const string homeUrl = "https://www.pornhub.com/";
        var extension = CreateExtension(new Dictionary<string, string>
        {
            [homeUrl] = "<html><body><div data-token=\"fixture-token\"></div></body></html>",
            ["api"] = JsonSerializer.Serialize(new
            {
                models = new[] { new { name = "Model Result", slug = "model-result" } },
                pornstars = new[] { new { name = "Star Result", slug = "star-result" } },
            }),
        });

        var results = await extension.SearchPerformersAsync(
            new ScraperRequest<string>("cove.community.scrapers.pornhub/performer", "result", new ScraperPermissions()),
            CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("Model Result", results[0].Name);
        Assert.Equal("https://www.pornhub.com/model/model-result", Assert.Single(results[0].Urls));
        Assert.Equal("Star Result", results[1].Name);
        Assert.Equal("https://www.pornhub.com/pornstar/star-result", Assert.Single(results[1].Urls));
    }

    private static PornhubScraperExtension CreateExtension(IReadOnlyDictionary<string, string>? responses = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(responses ?? new Dictionary<string, string>()))
            .BuildServiceProvider();

        var extension = new PornhubScraperExtension();
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
            if (url.StartsWith("https://www.pornhub.com/api/v1/video/search_autocomplete", StringComparison.OrdinalIgnoreCase)
                && responses.TryGetValue("api", out var apiJson))
            {
                return CreateResponse(apiJson, request);
            }

            if (responses.TryGetValue(url, out var content))
                return CreateResponse(content, request);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
        }

        private static Task<HttpResponseMessage> CreateResponse(string content, HttpRequestMessage request)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            });
    }
}
