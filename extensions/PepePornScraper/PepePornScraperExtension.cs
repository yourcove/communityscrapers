// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/PepePorn/PepePorn.yml; scrapers/PepePorn/PepePorn.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class PepePornScraperExtension()
    : FakNetworkScraperBase("cove.community.scrapers.pepeporn", "PepePorn Scraper", "pepeporn", "PepePorn");