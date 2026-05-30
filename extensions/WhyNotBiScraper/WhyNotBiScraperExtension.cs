// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/WhyNotBi/WhyNotBi.yml; scrapers/WhyNotBi/WhyNotBi.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class WhyNotBiScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.whynotbi", "Why Not Bi Scraper", "Why Not Bi", ["whynotbi"], "Why Not Bi");