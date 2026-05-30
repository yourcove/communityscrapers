using System.Net;
using Cove.Core.DTOs;
using Cove.Extensions.CommunityScrapers;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Yaml.Scrapers.Tests;

public sealed class PornhubLiveSmokeTests
{
    private const string LiveUrlEnvironmentVariable = "COVE_LIVE_PORNHUB_URL";
    private const string SceneScraperId = "cove.community.scrapers.pornhub/scene";

    [Fact]
    public async Task ScrapeSceneAsync_LivePornhubUrl_ReturnsPrimaryMetadata()
    {
        var url = Environment.GetEnvironmentVariable(LiveUrlEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(url))
            return;

        using var httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        httpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        var extension = CreateExtension(httpClient);
        var result = await extension.ScrapeSceneAsync(
            new ScraperRequest<SceneScrapeInput>(
                SceneScraperId,
                new SceneScrapeInput { Url = url },
                new ScraperPermissions()),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result!.Title));
        Assert.Contains("681e57ee89ed9", result.Code ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Urls, scrapedUrl => scrapedUrl.Contains("viewkey=681e57ee89ed9", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(result.ImageUrl));
        Assert.True(result.PerformerNames.Count > 0 || result.TagNames.Count > 0 || !string.IsNullOrWhiteSpace(result.StudioName));
    }

    private static PornhubScraperExtension CreateExtension(HttpClient httpClient)
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpClientFactory>(new LiveHttpClientFactory(httpClient))
            .BuildServiceProvider();

        var extension = new PornhubScraperExtension();
        extension.InitializeAsync(services).GetAwaiter().GetResult();
        return extension;
    }

    private sealed class LiveHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }
}