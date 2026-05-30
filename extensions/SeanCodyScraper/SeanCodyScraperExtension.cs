// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/SeanCody/SeanCody.yml; scrapers/SeanCody/SeanCody.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class SeanCodyScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.seancody", "Sean Cody Scraper", "Sean Cody", ["seancody"], "Sean Cody");