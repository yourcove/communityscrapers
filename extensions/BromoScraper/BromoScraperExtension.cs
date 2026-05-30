// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/Bromo/Bromo.yml; scrapers/Bromo/Bromo.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class BromoScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.bromo", "Bromo Scraper", "Bromo", ["bromo"]);