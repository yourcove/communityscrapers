using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.Interfaces;
using Cove.Plugins;

namespace Cove.Yaml.Scrapers.Tests;

public class YamlScraperPackDiscoveryTests
{
    [Fact]
    public void AllYamlPacks_ContributeAtLeastOneScraper()
    {
        var yamlDir = LocateYamlExtensionsDir();
        var manifestIds = Directory.EnumerateFiles(yamlDir, "extension.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(yamlDir, "extension.json", SearchOption.AllDirectories))
            .Select(ReadManifestId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var service = CreateService();
        var discoveredPackIds = service.GetScrapers()
            .Select(static scraper => scraper.Id.Split('/', 2)[0])
            .Where(id => id.StartsWith("cove.community.scrapers.yaml.", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = manifestIds
            .Where(id => !discoveredPackIds.Contains(id!))
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(manifestIds);
        Assert.True(missing.Count == 0, $"Missing scraper registrations for: {string.Join(", ", missing)}");
    }

    private static string? ReadManifestId(string filePath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
        return doc.RootElement.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;
    }

    private static ScraperService CreateService()
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
            new NoopHttpClientFactory(),
            extensionManager);
    }

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

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new NoopHttpMessageHandler())
            {
                BaseAddress = new Uri("https://example.invalid/"),
            };
    }

    private sealed class NoopHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = request,
            });
    }
}