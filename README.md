# Cove Community Scrapers

Community-maintained scraper extensions published through the official Cove extension registry.

## Extensions

- `cove.community.scrapers` - manifest-only bundle that installs the common scraper set.
- `cove.community.scrapers.common-audio` - Soundgasm and Whyp audio metadata extraction.
- `cove.community.scrapers.common-text` - Literotica story metadata extraction.
- `cove.community.scrapers.reddit` - Reddit and Redgifs metadata extraction.
- `cove.community.scrapers.ytdlp` - generic scene metadata extraction through `yt-dlp`.

## YAML Scrapers

YAML scraper files live in `extensions/YamlVideoScrapers/scrapers/` and are shipped as a registry-installable scraper pack.

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

Each extension has its own release tag prefix:

- `common/v1.0.0`
- `common-audio/v1.0.0`
- `common-text/v1.0.0`
- `reddit/v1.0.0`
- `ytdlp/v1.0.0`

The workflow packages only the tagged extension and uploads a zip named `<extension-id>-<version>.zip` for the registry.
