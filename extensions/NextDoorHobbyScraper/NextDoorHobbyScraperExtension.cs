// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
// Upstream files: scrapers/NextDoorHobby/NextDoorHobby.yml; scrapers/NextDoorHobby/NextDoorHobby.py
namespace Cove.Extensions.CommunityScrapers;

public sealed class NextDoorHobbyScraperExtension()
    : AyloNetworkScraperBase("cove.community.scrapers.nextdoorhobby", "NextDoorHobby Scraper", "NextDoorHobby", ["nextdoorhobby"], "NextDoorHobby");