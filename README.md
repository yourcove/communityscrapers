# Cove Community Scrapers

Community-maintained scraper extensions published through the official Cove extension registry.

## Extensions

- `cove.official.scrapers` - manifest-only bundle that installs the common scraper set.
- `cove.official.scrapers.common-audio` - Soundgasm and Whyp audio metadata extraction.
- `cove.official.scrapers.common-text` - Literotica story metadata extraction.
- `cove.official.scrapers.reddit` - Reddit and Redgifs metadata extraction.
- `cove.official.scrapers.ytdlp` - generic scene metadata extraction through `yt-dlp`.
- `cove.official.scrapers.yaml-video` - installable YAML scraper pack for video sites.

## YAML Scrapers

YAML scraper files live in `extensions/YamlVideoScrapers/scrapers/` and are shipped as a registry-installable scraper pack.

- `Pornhub.yml` - local YAML scraper source preserved from `%LocalAppData%\cove\scrapers`.
- `Xvideos.yml` - local YAML scraper source preserved from `%LocalAppData%\cove\scrapers`.

The pack uses `kind: "scraper-pack"`, has no DLL, and is enabled, disabled,
updated, and uninstalled through the same registry flow as compiled extensions.

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
- `yaml-video/v1.0.0`

The workflow packages only the tagged extension and uploads a zip named `<extension-id>-<version>.zip` for the registry.
