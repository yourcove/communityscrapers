// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/MadLifes/MadLifes.yml; scrapers/MadLifes/MadLifes.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class MadLifesScraperExtension()
    : FakNetworkScraperBase("cove.community.scrapers.madlifes", "MadLifes Scraper", "madlifes", "MadLifes");