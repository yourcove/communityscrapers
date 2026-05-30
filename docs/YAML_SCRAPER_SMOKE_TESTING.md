# YAML Scraper Smoke Testing

This repo includes a live smoke runner for the generated YAML scraper packs:

```powershell
npm run scrapers:smoke -- -- --limit 10
npm run scrapers:smoke -- -- --live --limit 10 --timeout 20
npm run scrapers:smoke -- -- --live --skip 100 --limit 10 --timeout 20
npm run scrapers:smoke -- -- --live --only xvideos --timeout 30
npm run scrapers:smoke -- -- --live --samples artifacts/verify/yaml-smoke-samples.json --timeout 30
```

The double delimiter is intentional: the first `--` belongs to npm, and the second is
forwarded to `dotnet run` so the remaining options reach the smoke runner.

The runner loads the real `extensions/yaml/*` scraper packs through Cove's `ScraperService`.
It is intentionally separate from the xUnit tests because a full pass performs network I/O,
can be slow, and can fail because of site availability, geo blocking, bot protection, or
broken upstream selectors.

## Modes

- Default mode is a dry run. It derives candidate base URLs from each scraper registration and
  writes a report without fetching sites or running the scraper.
- `--live` fetches candidate sites, tries to discover a content URL from same-host links, runs
  the Cove scraper against that URL, and records the result.
- `--limit <n>` runs only the first `n` scraper registrations.
- `--skip <n>` skips the first `n` registrations before applying `--limit`, which is useful
  for sampling different parts of the generated catalog.
- `--only <text>` filters by scraper id or display name.
- `--timeout <seconds>` caps both discovery and scrape calls.
- `--report-dir <path>` changes where reports and snapshots are written.
- `--samples <json>` supplies known-good sample URLs keyed by either full scraper id or extension
  id. This is the most reliable way to turn a smoke run into a repeatable compatibility suite.

Example sample file:

```json
{
  "cove.community.scrapers.yaml.xvideos": "https://www.xvideos.com/video.abcdef/test_scene",
  "cove.community.scrapers.yaml.example/example": "https://example.com/video/123"
}
```

## Output

The runner writes:

- `artifacts/verify/yaml-smoke/yaml-smoke-report.json`
- `artifacts/verify/yaml-smoke/yaml-smoke-report.md`
- `artifacts/verify/yaml-smoke/snapshots/*.bin` for fetched responses

Statuses:

- `pass`: scraper returned the expected primary field for the entity type, such as `Title`
  for a scene or `Name` for a performer.
- `weak-result`: scraper returned fields but is missing the expected primary field for that
  entity type (for example, a scene with only `URL` and no `Title`).
- `empty-result`: the engine ran but returned no useful data.
- `engine-error`: Cove's YAML engine threw while scraping.
- `network-error`: the site or HTTP layer failed.
- `timeout`: discovery or scraping exceeded the timeout.
- `no-sample-url`: the runner could not derive a candidate URL.
- `not-run-dry-run`: dry-run placeholder result.

Use the status plus captured snapshots to decide whether a failure is caused by stale upstream
selectors, live-site blocking, missing Cove YAML engine support, or bad generated metadata.
