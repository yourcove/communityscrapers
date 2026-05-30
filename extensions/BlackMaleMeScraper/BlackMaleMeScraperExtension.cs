// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/BlackMaleMe/BlackMaleMe.yml; scrapers/BlackMaleMe/BlackMaleMe.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class BlackMaleMeScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.blackmaleme", "Black Male Me Scraper", "Black Male Me", ["blackmaleme"], "Black Male Me");