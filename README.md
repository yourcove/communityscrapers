# Cove Community Scrapers

Community-maintained scraper extensions published through the official Cove extension registry.

## Extensions

- `cove.community.scrapers` - manifest-only bundle that installs the common scraper set.
- `cove.community.scrapers.blackmaleme` - Black Male Me metadata extraction.
- `cove.community.scrapers.bromo` - Bromo metadata extraction.
- `cove.community.scrapers.common-audio` - Soundgasm and Whyp audio metadata extraction.
- `cove.community.scrapers.common-text` - Literotica story metadata extraction.
- `cove.community.scrapers.erotik` - Erotik group metadata extraction.
- `cove.community.scrapers.fakings` - FaKings metadata extraction.
- `cove.community.scrapers.madlifes` - MadLifes metadata extraction.
- `cove.community.scrapers.nextdoorhobby` - Next Door Hobby metadata extraction.
- `cove.community.scrapers.pepeporn` - PepePorn metadata extraction.
- `cove.community.scrapers.pornhub` - Pornhub metadata extraction.
- `cove.community.scrapers.reddit` - Reddit and Redgifs metadata extraction.
- `cove.community.scrapers.seancody` - Sean Cody metadata extraction.
- `cove.community.scrapers.tube8vip` - Tube8VIP metadata extraction.
- `cove.community.scrapers.whynotbi` - Why Not Bi metadata extraction.
- `cove.community.scrapers.ytdlp` - generic scene metadata extraction through `yt-dlp`.

## YAML Scrapers

Generated YAML scraper packs live under `extensions/yaml/`. Each generated pack uses
`kind: "scraper-pack"`, has no DLL, and is installed from source through the registry.

## Development

Clone this repository beside `cove` to build against local Cove contracts, or set `UseLocalCovePlugins=false` to consume published packages.

```powershell
npm run validate:extensions
dotnet build extensions/CommonAudioScraper/CommonAudioScraper.csproj
dotnet build extensions/CommonTextScraper/CommonTextScraper.csproj
dotnet build extensions/RedditScraper/RedditScraper.csproj
dotnet build extensions/YtDlpScraper/YtDlpScraper.csproj
```

## Releases

Create release tags with the lowercase `tagPrefix` from `extensions/catalog.json`, for example `common/v1.0.0` or `pornhub/v1.0.0`.

The workflow accepts any `<tagPrefix>v<semver>` tag, packages only the matching catalog entry, and uploads a zip named `<extension-id>-<version>.zip` for the registry.
