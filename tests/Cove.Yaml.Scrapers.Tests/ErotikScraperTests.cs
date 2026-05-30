using System.Net;
using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Extensions.CommunityScrapers;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Yaml.Scrapers.Tests;

public sealed class ErotikScraperTests
{
    private const string GroupUrl = "https://www.erotik.com/en/movies/fixture-movie";
    private const string ApiUrl = "https://api.erotik.com/content/movies?idList[]=fixture-id";

    private static readonly string GroupHtml = """
        <html>
          <head><meta name="id" content="fixture-id"></head>
          <body></body>
        </html>
        """;

    private static readonly string ApiJson = JsonSerializer.Serialize(new[]
    {
        new
        {
            title = new { en = "Fixture Group" },
            description = new { en = "<p>Fixture <strong>synopsis</strong>.</p>" },
            studio = new { name = "Fixture Studio" },
            image = new { @default = "https://images.example.invalid/front.jpg" },
            directors = new[] { new { name = "Director One" }, new { name = "Director Two" } },
            categories = new[] { new { sortOrder = 0, name = new { en = "Tag One" } }, new { sortOrder = -1, name = new { en = "Hidden Tag" } } },
            durationSeconds = 5400,
            rating = 4,
            releaseYear = 2024,
            url = new { en = "https://www.erotik.com/en/movies/fixture-movie", de = "https://www.erotik.com/de/movies/fixture-movie" },
        },
    });

    [Fact]
    public async Task ScrapeGroupAsync_ParsesErotikGroupMetadata()
    {
        var extension = CreateExtension(new Dictionary<string, string>
        {
            [GroupUrl] = GroupHtml,
            [ApiUrl] = ApiJson,
        });

        var result = await extension.ScrapeGroupAsync(
            new ScraperRequest<GroupScrapeInput>(
                "cove.community.scrapers.erotik/group",
                new GroupScrapeInput { Url = GroupUrl },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("Fixture Group", result!.Name);
        Assert.Equal("Fixture synopsis.", result.Synopsis);
        Assert.Equal("Fixture Studio", result.StudioName);
        Assert.Equal("https://images.example.invalid/front.jpg", result.ImageUrl);
        Assert.Equal("Director One, Director Two", result.Director);
        Assert.Equal(5400, result.Duration);
        Assert.Equal(4, result.Rating);
        Assert.Equal("2024", result.Date);
        Assert.Contains("Tag One", result.TagNames);
        Assert.DoesNotContain("Hidden Tag", result.TagNames);
        Assert.Contains("https://www.erotik.com/en/movies/fixture-movie", result.Urls);
    }

    private static ErotikScraperExtension CreateExtension(IReadOnlyDictionary<string, string> responses)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new FakeHttpClientFactory(responses))
            .BuildServiceProvider();

        var extension = new ErotikScraperExtension();
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