// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/Tube8Vip/Tube8Vip.yml; scrapers/Tube8Vip/Tube8Vip.py
using System.Text.Json;

namespace Cove.Extensions.CommunityScrapers;

public sealed class Tube8VipScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.tube8vip", "Tube8Vip Scraper", "Tube8Vip", ["tube8vip"], "Tube8Vip")
{
    protected override string NormalizeUrl(string url, JsonElement source)
        => url.Replace("elite.com", "tube8vip.com", StringComparison.OrdinalIgnoreCase);
}