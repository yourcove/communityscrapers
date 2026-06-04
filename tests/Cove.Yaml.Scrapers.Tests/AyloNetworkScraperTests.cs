using System.Net;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Extensions.CommunityScrapers;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Yaml.Scrapers.Tests;

public sealed class AyloNetworkScraperTests
{
    private const string SceneUrl = "https://www.seancody.com/scene/1234/fixture-scene";
    private const string SceneApiUrl = "https://site-api.project1service.com/v2/releases/1234";
    private const string PerformerUrl = "https://www.seancody.com/model/42/fixture-performer";
    private const string PerformerApiUrl = "https://site-api.project1service.com/v1/actors/42";

    private static readonly object Movie = new
    {
        id = 999,
        type = "movie",
        brand = "seancody",
        title = "Fixture Movie",
        description = "<p>Fixture movie synopsis.</p>",
        dateReleased = "2024-04-03T00:00:00+0000",
        durationSeconds = 7200,
        rating = 5,
        images = new { cover = new[] { new { xx = new { url = "https://images.example.invalid/m=eaSaaWx/movie.jpg" } } } },
        brandMeta = new { displayName = "Wrong Studio" },
        tags = new[] { new { id = 90, name = "Athletic" } },
    };

    private static readonly string SceneJson = JsonSerializer.Serialize(new
    {
        result = new
        {
            id = 1234,
            type = "scene",
            brand = "seancody",
            title = "Fixture Scene",
            description = "<p>Fixture <strong>scene</strong>.</p>",
            dateReleased = "2025-01-02T00:00:00+0000",
            images = new { poster = new[] { new { xx = new { url = "https://images.example.invalid/m=eaSaaWx/poster.jpg" } } } },
            parent = Movie,
            collections = new[] { new { name = "API Studio" } },
            brandMeta = new { displayName = "Brand Studio" },
            actors = new[] { new { id = 42, name = "Fixture Performer", gender = "Male" } },
            tags = new[] { new { id = 90, name = "Athletic" }, new { id = 999, name = "Custom Tag" } },
        },
    });

    private static readonly string PerformerJson = JsonSerializer.Serialize(new
    {
        result = new
        {
            id = 42,
            name = "Fixture Performer",
            gender = "Male",
            aliases = new[] { "Fixture Performer", "Alias One" },
            bio = "<p>Fixture performer bio.</p>",
            height = 70,
            weight = 180,
            birthday = "1990-06-07T00:00:00+0000",
            birthPlace = "Example City, US",
            measurements = "40-32-38",
            images = new { master_profile = new { primary = new { xx = new { url = "https://images.example.invalid/profile.jpg" } } } },
            tags = new[] { new { id = 378, name = "White" } },
        },
    });

    [Fact]
    public async Task ScrapeVideoAsync_ParsesAyloVideoMetadata()
    {
        var extension = CreateExtension(new SeanCodyScraperExtension(), new Dictionary<string, FakeResponse>
        {
            ["https://www.seancody.com/"] = FakeResponse.Token("fixture-token"),
            [SceneApiUrl] = FakeResponse.Json(SceneJson),
        });

        var result = await extension.ScrapeVideoAsync(
            new ScraperRequest<VideoScrapeInput>(
                "cove.community.scrapers.seancody/video",
                new VideoScrapeInput { Url = SceneUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Scene", result!.Title);
        Assert.Equal("1234", result.Code);
        Assert.Equal("2025-01-02", result.Date);
        Assert.Equal("Fixture scene.", result.Details);
        Assert.Equal("Sean Cody", result.StudioName);
        Assert.Equal("https://images.example.invalid/poster.jpg", result.ImageUrl);
        Assert.Equal("https://www.seancody.com/scene/1234/fixture-scene", Assert.Single(result.Urls));
        Assert.Contains("Fixture Performer", result.PerformerNames);
        Assert.Contains("Athletic Woman", result.TagNames);
        Assert.Contains("Custom Tag", result.TagNames);
    }

    [Fact]
    public async Task ScrapePerformerAsync_ParsesAyloPerformerMetadata()
    {
        var extension = CreateExtension(new SeanCodyScraperExtension(), new Dictionary<string, FakeResponse>
        {
            ["https://www.seancody.com/"] = FakeResponse.Token("fixture-token"),
            [PerformerApiUrl] = FakeResponse.Json(PerformerJson),
        });

        var result = await extension.ScrapePerformerAsync(
            new ScraperRequest<PerformerScrapeInput>(
                "cove.community.scrapers.seancody/performer",
                new PerformerScrapeInput { Url = PerformerUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Performer", result!.Name);
        Assert.Equal("1990-06-07", result.Birthdate);
        Assert.Equal("USA", result.Country);
        Assert.Equal(178, result.HeightCm);
        Assert.Equal(82, result.Weight);
        Assert.Equal("40-32-38", result.Measurements);
        Assert.Equal("Fixture performer bio.", result.Details);
        Assert.Equal("https://images.example.invalid/profile.jpg", result.ImageUrl);
        Assert.Equal("Alias One", Assert.Single(result.Aliases));
        Assert.Contains("White Man", result.TagNames);
    }

    [Fact]
    public async Task ScrapeGroupAsync_UsesParentMovieFromSceneResponse()
    {
        var extension = CreateExtension(new SeanCodyScraperExtension(), new Dictionary<string, FakeResponse>
        {
            ["https://www.seancody.com/"] = FakeResponse.Token("fixture-token"),
            [SceneApiUrl] = FakeResponse.Json(SceneJson),
        });

        var result = await extension.ScrapeGroupAsync(
            new ScraperRequest<GroupScrapeInput>(
                "cove.community.scrapers.seancody/group",
                new GroupScrapeInput { Url = SceneUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Movie", result!.Name);
        Assert.Equal("Fixture movie synopsis.", result.Synopsis);
        Assert.Equal("Sean Cody", result.StudioName);
        Assert.Equal(7200, result.Duration);
        Assert.Equal(5, result.Rating);
        Assert.Equal("https://images.example.invalid/movie.jpg", result.ImageUrl);
    }

    [Fact]
    public void AyloPorts_AdvertiseExpectedScrapers()
    {
        Assert.Equal(4, new BromoScraperExtension().GetScrapers().Count);
        Assert.Contains(new BlackMaleMeScraperExtension().GetScrapers(), scraper => scraper.Id == "cove.community.scrapers.blackmaleme/video");
        Assert.Contains(new NextDoorHobbyScraperExtension().GetScrapers(), scraper => scraper.Id == "cove.community.scrapers.nextdoorhobby/video");
        Assert.Contains(new SeanCodyScraperExtension().GetScrapers(), scraper => scraper.Id == "cove.community.scrapers.seancody/performer");
        Assert.Contains(new Tube8VipScraperExtension().GetScrapers(), scraper => scraper.Id == "cove.community.scrapers.tube8vip/group");
        Assert.Contains(new WhyNotBiScraperExtension().GetScrapers(), scraper => scraper.Id == "cove.community.scrapers.whynotbi/group");
    }

    private static TExtension CreateExtension<TExtension>(TExtension extension, IReadOnlyDictionary<string, FakeResponse> responses)
        where TExtension : IScraperProvider
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(responses))
            .BuildServiceProvider();

        extension.InitializeAsync(services).GetAwaiter().GetResult();
        return extension;
    }

    private sealed record FakeResponse(string Content, string? InstanceToken = null)
    {
        public static FakeResponse Json(string content) => new(content);

        public static FakeResponse Token(string token) => new("<html></html>", token);
    }

    private sealed class FakeHttpClientFactory(IReadOnlyDictionary<string, FakeResponse> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FakeHttpMessageHandler(responses));
    }

    private sealed class FakeHttpMessageHandler(IReadOnlyDictionary<string, FakeResponse> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (!responses.TryGetValue(url, out var fake))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(fake.Content),
                RequestMessage = request,
            };

            if (!string.IsNullOrWhiteSpace(fake.InstanceToken))
                response.Headers.TryAddWithoutValidation("Set-Cookie", $"instance_token={fake.InstanceToken}; Path=/");

            return Task.FromResult(response);
        }
    }
}