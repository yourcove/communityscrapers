// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/FaKings/FaKings.yml; scrapers/FaKings/FaKings.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class FaKingsScraperExtension()
    : FakNetworkScraperBase("cove.community.scrapers.fakings", "FaKings Scraper", "fakings", "FaKings");