# Stash CommunityScrapers → Cove Porting Plan

> **Audience:** the agent executing this work. Follow this document top to bottom. It is
> prescriptive on purpose. Do not improvise structure; when something is genuinely
> ambiguous, prefer the smallest change that satisfies the stated goal and record the
> decision in the per-batch tracking file (see §9).

## 0. Goals (what "done" means)

1. **YAML extension cleanup** — make a single `.yml` scraper its own self-contained
   extension with the *minimum* scaffolding possible, and make registering many of them
   cheap (no hand-written boilerplate per scraper). (§3)
2. **Port the stash YAML-only scrapers** — copy compatible `.yml` scrapers into this repo
   and **test each one** against Cove's YAML engine. Where a scraper exposes a real gap in
   Cove's engine, fix Cove (or record the gap). (§4, §6)
3. **Port the code-based scrapers** — scrapers that use `action: script` (Python) or other
   code must be re-implemented as standard Cove `.NET` scraper extensions. (§5)
4. **License compliance** — every copied or ported file carries the AGPL-3.0 attribution
   header pointing at the upstream repo. (§2)

### Repos involved (absolute paths on this machine)

| Role | Path |
| --- | --- |
| Target repo (this one) | `c:\Users\tyler\source\repos\communityscrapers` |
| Upstream stash scrapers | `C:\Users\tyler\source\repos\CommunityScrapers-master-stash` |
| Cove app (engine + tests) | `C:\Users\tyler\source\repos\cove` |
| Official extension registry | `C:\Users\tyler\source\repos\officialextensionregistry` |

Upstream license: **AGPL-3.0**. You are allowed to change Cove and the registry repos as
needed.

---

## 1. Current state (read this before touching anything)

### 1.1 How Cove runs YAML scrapers

The engine lives in **`cove/src/Cove.Api/Services/ScraperService.cs`** (~2370 lines). It
parses the stash YAML format with `YamlDotNet` and evaluates XPath with `HtmlAgilityPack`.

**Supported today** (verified by reading the source):

- Actions: `scrapeXPath` and `scrapeJson`.
- Entities: `scene`, `performer`, `gallery`, `image`, `group`, `audio`, `text`, `movie`.
- Entry points: `*ByURL`, `*ByName`, `*ByFragment`, plus `sceneByQueryFragment`.
- `xPathScrapers` and `jsonScrapers` maps with `common` + per-entity sections.
- Selector features: `selector`, `fixed`, `concat`, `split`, `postProcess`.
- `postProcess` steps: **`replace`, `parseDate`, `map` only**
  (see `ApplyPostProcesses`, ~line 2172).
- `driver.headers` and `driver.cookies`.
- URL placeholder substitution: `{url}`, `{}`, `{name}`, `{query}`, `{filename}`,
  and `queryURLReplace` regex rewrites.

**NOT supported today (these are the gaps that drive Cove fixes):**

| Gap | Upstream usage count | Where to fix in Cove |
| --- | --- | --- |
| `action: script` (Python/JS) | ~720 occurrences | Cannot be YAML — must port to .NET (§5) |
| `postProcess: subScraper` | 41 | `ApplyPostProcesses` in `ScraperService.cs` |
| `postProcess: feetToCm` | 62 | `ApplyPostProcesses` |
| `postProcess: lbToKg` | 36 | `ApplyPostProcesses` |
| `parseDate` Go layouts | hundreds | `ApplyParseDate` (see §6.1 — **highest priority Cove fix**) |

Public methods you will use for testing (no HTTP server required):

- `ScraperService.ScrapeUrlAutoDetailedAsync(url, entityType, ct)` → returns the winning
  scraper id, the scraped dictionary, and per-scraper attempt diagnostics.
- `ScraperService.ScrapeUrlAsync(scraperId, entityType, url, ct)` → run one specific scraper.
- Existing tests: `cove/src/Cove.Tests/ScraperServiceTests.cs` (copy its setup pattern).

### 1.2 How extensions are declared in this repo

- **`extensions/catalog.json`** enumerates every extension (id, path, tagPrefix, flags).
- A YAML scraper extension is a **scraper-pack**: `kind: "scraper-pack"`, marked
  `manifestOnly: true` + `contentOnly: true` in the catalog, with a `scrapers/` folder of
  `.yml`/`.yaml` files. Generated source-tracked packs live under `extensions/yaml/`.
- **`scripts/validate-extension-repo.mjs`** validates the catalog + manifests.
- **`.github/workflows/build.yml`** builds/releases per-extension, keyed on a `tagPrefix`
  (e.g. `common-audio/v*`). manifestOnly extensions skip the .NET build and just zip the
  manifest + `scrapers/` payload.
- **`.NET` scrapers** implement `IScraperProvider` (from `Cove.Plugins`), return
  `ScrapedSceneDto` / `ScrapedPerformerDto` / etc., and share helpers in
  `extensions/Shared/OfficialDownloaderUtilities.cs`. Reference implementations:
  `extensions/RedditScraper`, `extensions/YtDlpScraper`, `extensions/CommonAudioScraper`,
  `extensions/CommonTextScraper`.

### 1.3 How the registry publishes extensions

`officialextensionregistry`:
- `index.json` is an **ID-only** list (CI keeps it in sync — do not hand-edit version data).
- `extensions/<extension-id>.json` is the canonical per-extension metadata with a
  `versions[]` array (`downloadUrl`, `minCoveVersion`, etc.). CI computes `checksum` and
  stamps `releasedAt`. `downloadUrl` points at a GitHub release ZIP in this repo.

### 1.4 Upstream inventory (for scoping)

- `scrapers/` has **638 top-level `.yml` files** and **171 sub-directories**.
- Sub-directories usually mean Python (`action: script`) and/or shared modules
  (`py_common/`, `AyloAPI/`, `Algolia/`, etc.). Treat them as §5 candidates by default.
- Many top-level `.yml` files are **mixed**: e.g. `sceneByURL` via `scrapeXPath` (portable)
  but `performerByName` via `action: script` (not portable). See §4.3 for how to handle.

---

## 2. License / attribution header (MANDATORY on every copied or ported file)

Every file that is copied or derived from upstream **must** start with an attribution
header. Use the exact text below (adjust the comment syntax per file type).

**For `.yml` scraper files** (prepend before `name:`):

```yaml
# Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
# Original source licensed under AGPL-3.0. Modifications for Cove are also AGPL-3.0.
# Upstream file: scrapers/<OriginalRelativePath>
```

**For ported `.cs` files** (top of file, above `using`s):

```csharp
// Ported from stashapp/CommunityScrapers (https://github.com/stashapp/CommunityScrapers/tree/master)
// Original source licensed under AGPL-3.0. This port for Cove is also distributed under AGPL-3.0.
// Upstream source: scrapers/<OriginalRelativePath>
```

Rules:
- Always fill in `<OriginalRelativePath>` with the real upstream path
  (e.g. `scrapers/Pornhub/Pornhub.yml`).
- Do **not** strip upstream author credits if present in the original file — keep them
  below the new header.
- `communityscrapers/LICENSE` is **already AGPL-3.0** (confirmed), so copying upstream
  AGPL-3.0 content into this repo is license-compatible. Keep it that way.

---

## 3. Part 1 — YAML extension cleanup (do this FIRST)

**Problem:** YAML scrapers were initially lumped into one placeholder pack, and adding a
scraper-pack meant hand-writing an `extension.json`, a `catalog.json` entry, a unique
`tagPrefix`, network permissions, and a registry file. That does not scale to hundreds of
single-scraper extensions.

**Target design:** one `.yml` = one extension, with the manifest and registration
**generated**, not hand-written.

### 3.1 Directory convention

Create a dedicated tree for single-scraper YAML packs:

```
extensions/
  yaml/
    <ScraperId>/                # kebab-case, derived from the yml name
      extension.json            # GENERATED
      scrapers/
        <ScraperId>.yml         # the ported scraper (with attribution header)
```

`<ScraperId>` rules: lowercase-kebab of the upstream scraper `name:` field
(e.g. `Pornhub` → `pornhub`, `XConfessions` → `xconfessions`). Extension id =
`cove.community.scrapers.yaml.<ScraperId>`.

### 3.2 Minimal manifest template

A single-scraper pack manifest should contain only what the validator + Cove require.
Target shape (the generator fills the bracketed values):

```json
{
  "id": "cove.community.scrapers.yaml.<ScraperId>",
  "name": "<Display Name> Scraper",
  "version": "0.0.0",
  "description": "YAML metadata scraper for <Display Name> (ported from stashapp/CommunityScrapers).",
  "author": "Cove Team",
  "url": "https://github.com/yourcove/communityscrapers",
  "kind": "scraper-pack",
  "minCoveVersion": "0.0.16",
  "categories": ["scraper", "metadata", "yaml-scraper"],
  "permissions": { "network": [<hosts derived from the yml url: entries>] }
}
```

Derive `network` hosts from the scraper's `*ByURL[].url` and `queryURL` values (host +
`*.host`). If hosts cannot be derived, fall back to `["*"]` and note it in the batch log.

### 3.3 Build a generator script (eliminates per-scraper boilerplate)

Add **`scripts/generate-yaml-scraper.mjs`** (Node, ESM, matches existing `.mjs` style). It
must:

1. Accept one or more input `.yml` paths (or `--from-dir <dir>` to batch a whole folder), and
   an optional `--id` override and `--force`.
2. Parse the YAML to read `name`, all `url:`/`queryURL:` hosts, and detect unsupported
   features. Scrapers whose entry points use `action: script`/`action: stash` are **skipped**
   (routed to §5) unless `--force` is passed — do not silently generate a broken pack.
3. Create `extensions/yaml/<ScraperId>/extension.json` from the template (§3.2), deriving the
   id from `name:` (kebab-cased, prefixed `cove.community.scrapers.yaml.`).
4. Copy the `.yml` into `extensions/yaml/<ScraperId>/scrapers/` **and inject the attribution
   header** (§2) if absent.

The generator does **not** touch `catalog.json` (YAML packs are auto-discovered, §3.4) or the
registry (that is a separate generated step, §3.5).

Wire the scripts into `package.json`:
```json
"scrapers:gen": "node scripts/generate-yaml-scraper.mjs",
"registry:gen": "node scripts/generate-registry-entries.mjs"
```

### 3.4 Catalog: auto-discovery (implemented)

YAML packs are **not** listed in `extensions/catalog.json`. `catalog.json` drives the CI
build/zip/release matrix, which only applies to compiled .NET extensions. YAML packs are
unversioned and source-served (§3.5), so they have no build/release step and do not belong
in that matrix.

Instead, `scripts/validate-extension-repo.mjs` **auto-discovers** every directory under
`extensions/yaml/*` after validating the catalog entries. Each discovered pack is validated
by `validateYamlScraperPack(folder)`:

- `id === "cove.community.scrapers.yaml.<folder>"`,
- `kind: scraper-pack`, no `entryDll`,
- `minCoveVersion`, `url`, and lowercase-kebab `categories` present,
- `scraperFiles` is a non-empty array whose entries exist inside the pack (no path
  traversal),
- each referenced `.yml` has the stashapp/CommunityScrapers attribution header, a top-level
  `name:`, and no `action: script`/`action: stash` (those route to §5).

The validator's final line reports both counts, e.g.
`Validated 6 extension catalog entries and 1 YAML scraper pack(s).`

### 3.5 Release strategy: none — unversioned, source-served (implemented)

YAML-only scrapers are **not versioned**: there are no tags, no zips, no CI release, and no
checksums. The `.yml` file in `main` is always the source of truth, exactly like the legacy
behavior of always loading the YAML from source.

This is implemented in Cove as a **source-tracked scraper-pack**. A registry entry with
`kind: scraper-pack` + a `sourceManifestUrl` and **no `versions[]` array** is treated by
Cove as source-served: Cove fetches the manifest live and downloads each file listed in the
manifest's `scraperFiles` field directly from raw GitHub (`GitHubExtensionRegistry`:
`IsSourcePack` / `BuildSourcePackDetailAsync` / `DownloadSourcePackAsync`). No zip, no
checksum, no release tag is involved, and `SelectRegistryVersion` only checks Cove-version
compatibility.

Registry entries are generated, not hand-written, by
**`scripts/generate-registry-entries.mjs`**:

- scans `extensions/yaml/*`,
- emits one `extensions/<extension-id>.json` per pack with `kind: scraper-pack`, a
  `sourceManifestUrl` pointing at the raw GitHub `extension.json` on `main`, and **no
  `versions[]`**,
- adds each id to the registry `index.json`.

Run `npm run registry:gen` for a dry run, or `npm run registry:gen -- --write` (optionally
`--registry <path>`) to write into the `officialextensionregistry` checkout.

### 3.6 Migrate the existing pack

The old placeholder pack has been retired. Generated source-tracked packs now live under
`extensions/yaml/`, while script-backed scrapers such as Pornhub are handled by compiled
.NET ports.

### 3.7 Exit criteria for Part 1

- `npm run validate:extensions` passes (catalog entries **and** YAML packs).
- `npm run scrapers:gen -- <path-to-a-test-yml>` produces a complete, valid pack with zero
  manual edits.
- `npm run registry:gen` emits a source-tracked (no-`versions`) entry for each pack.
- The in-repo .NET test project (§4.4) discovers and scrapes the generated pack through
  Cove's real engine.

---

## 4. Part 2 — Port the YAML-only scrapers (copy + test)

Process upstream `.yml` scrapers in **small batches** (suggest 15–25 per batch) so each is
actually verified, not bulk-dumped.

### 4.1 Triage each candidate

For an upstream `.yml`, classify it:

- **Pure-compatible** → only uses supported actions (`scrapeXPath`/`scrapeJson`) and
  supported `postProcess` (`replace`/`parseDate`/`map`) and supported entry points. → §4.2.
- **Compatible-after-Cove-fix** → uses `subScraper`/`feetToCm`/`lbToKg`/Go-layout
  `parseDate`. → fix Cove first (§6), then treat as compatible.
- **Mixed** → some entry points are `action: script`. → §4.3.
- **Code-only** → primarily `action: script`, or lives in a sub-directory with Python. → §5.

Use this detection command as a starting filter (adjust as needed):
```powershell
Select-String -Path <file.yml> -Pattern 'action:\s*script|subScraper|feetToCm|lbToKg'
```

### 4.2 Port a pure-compatible scraper

1. Run `npm run scrapers:gen -- "<upstream>\scrapers\<Name>.yml"` (generator copies +
   injects header + creates the pack + registers it).
2. **Test it** (§4.4). Do not mark done until a live scrape returns sane fields.

### 4.3 Handle mixed scrapers

If a scraper is mostly compatible but has one `action: script` entry point
(commonly `performerByName`):
- Remove the `script` entry point from the ported `.yml` and keep the XPath/JSON parts.
- Add a comment in the ported file noting which capability was dropped and why.
- If the dropped capability is important (e.g. it was the scraper's main feature), instead
  route the whole scraper to §5.
- Record the decision in the batch log.

### 4.4 Testing methodology (REQUIRED per scraper)

You cannot rely on "it should just work." Verify each scraper. Two acceptable methods:

**Method A — xUnit harness in THIS repo (preferred, repeatable):**

Tests live in **this repo**, not in Cove — Cove's own test suite must not depend on these
extensions. The project is
`tests/Cove.Yaml.Scrapers.Tests/Cove.Yaml.Scrapers.Tests.csproj`. It references the local
Cove projects via the sibling checkout (`..\..\..\cove\src\{Cove.Api,Cove.Core,Cove.Plugins}`),
so it builds the engine fresh and requires `repos\cove` next to `repos\communityscrapers`.

The harness (see `XvideosScraperTests.cs` for the reference pattern):
  1. locates the repo's real `extensions/yaml` folder and calls
     `ExtensionManager.DiscoverExtensions(...)` on it (manifest-only scraper-packs are
     enabled by default and keyed by manifest id, not folder name),
  2. constructs `ScraperService` with a `FakeHttpClientFactory` that returns canned HTML
     keyed by URL,
  3. calls `ScrapeUrlAutoAsync(sampleUrl, entityType)` (or `ScrapeUrlAsync`/
     `ScrapeUrlAutoDetailedAsync`),
  4. asserts the expected fields are non-empty and correctly shaped (Title/URL for scenes,
     Name for performers, dates as `yyyy-MM-dd`).

Run with `dotnet test tests/Cove.Yaml.Scrapers.Tests`. If a running Cove instance locks the
shared `bin`, build/test with `-o ./artifacts/verify/<name>` and delete the temp output
afterward.

**Method B — live run via the running app:**
- Build + run Cove, install the generated pack locally
  (`%LocalAppData%\cove\extensions\<extension-id>`), and use the UI "Scrape with URL"
  flow or the relevant `*/scrape-url` API endpoint with a real sample URL.
- Confirm via logs which scraper matched and inspect the returned metadata.

**Picking sample URLs:** take them from the upstream scraper's `url:` patterns. If a site is
unreachable/age-gated/down, mark the scraper **`unverified`** in the batch log (ported but
not confirmed) rather than claiming success.

**What counts as a pass:** for the primary entity the scraper targets, the expected core
fields are populated and correctly shaped (dates as `yyyy-MM-dd`, no raw HTML leaking into
text fields, performer/tag lists split correctly).

### 4.5 When a scraper fails

Determine the cause:
- **Engine gap** (unsupported feature) → fix Cove (§6), re-test, and note the fix.
- **Site changed since upstream wrote it** → if trivially fixable in the `.yml`, fix and note
  it; otherwise mark `unverified` and move on (don't rabbit-hole).
- **Selector/postProcess bug in upstream** → fix in the ported `.yml` and note it.

---

## 5. Part 3 — Port code-based (Python/script) scrapers to .NET

These cannot be YAML. Re-implement them as standard Cove `.NET` scraper extensions.

### 5.1 When to do this

- The scraper's only/primary capability uses `action: script`, OR
- It depends on a shared Python module (`py_common`, `AyloAPI`, `Algolia`, `ModelCentroAPI`,
  etc.), OR
- It needs logic the YAML engine can't express (auth flows, pagination, signing, JSON
  graph traversal beyond `jsonScrapers`).

Prioritize porting the **shared API modules first** (e.g. an `AyloAPI` helper powers many
network scrapers), since one ported helper unlocks a family of scrapers.

### 5.2 Implementation pattern

Follow the existing extensions as templates:
- `extensions/RedditScraper/RedditScraperExtension.cs` — JSON API + multiple entities.
- `extensions/YtDlpScraper/YtDlpScraperExtension.cs` — external process + settings/env.
- `extensions/CommonTextScraper/CommonTextScraperExtension.cs` — HTML/JSON-LD parsing.

Each ported .NET scraper:
1. New project `extensions/<Name>Scraper/<Name>Scraper.csproj` (copy an existing csproj;
   `Directory.Build.props` already sets framework + Cove package versions).
2. `extension.json` with a stable id `cove.community.scrapers.<name>`, an `entryDll`, real
   `network` permissions, and any `settings` / `externalDependencies`.
3. A class implementing `IScraperProvider`, exposing `ScraperDescriptor`(s) with correct
   `ScraperEntity`, `ScraperCapabilities` (ByUrl/ByName/ByFragment), URL patterns, and
   `ScraperRiskLevel`.
4. Reuse `extensions/Shared/OfficialDownloaderUtilities.cs`; add shared helpers there if a
   family of scrapers needs them.
5. Add the project to `CommunityScrapers.slnx`, a catalog entry (with a `tagPrefix`), and a
   registry file.
6. **Attribution header** (§2) at the top of every ported `.cs` file, citing the upstream
   Python path.

### 5.3 Testing .NET scrapers

Add unit tests in this repo (or a small console smoke test) and verify against a live
sample URL exactly as in §4.4. Reuse the existing scraper extensions' test approach if
present.

---

## 6. Cove engine fixes (do these as gaps are hit; some are known up front)

All edits are in `cove/src/Cove.Api/Services/ScraperService.cs` unless noted. After any
engine change, add/extend tests in `cove/src/Cove.Tests/ScraperServiceTests.cs` and rebuild
Cove.Api before re-testing scrapers.

### 6.1 `parseDate` Go-layout translation (HIGH PRIORITY — fix before bulk YAML testing)

`ApplyParseDate` (~line 2226) currently passes the stash format string straight into
.NET `DateTime.TryParseExact`. Stash uses **Go reference-date layouts** (`January 2, 2006`,
`2006-01-02`, `01/02/2006`, `02 Jan 2006`, `unix`, …), which are NOT .NET custom format
specifiers. Today only the *general* `DateTime.TryParse` fallback rescues some inputs, and
it mis-parses ambiguous formats (e.g. `02/01/2006` day-first vs month-first).

Fix: translate the Go reference layout to a .NET format string (map the reference field
values `2006`→`yyyy`, `01`→`MM`, `02`→`dd`, `15`→`HH`, `04`→`mm`, `05`→`ss`, `Jan`→`MMM`,
`January`→`MMMM`, `Mon`→`ddd`, `Monday`→`dddd`, `-07:00`/`Z07:00`→`zzz`/`K`, `PM`→`tt`,
etc.), and special-case `unix`/`unixMilli`. Then `TryParseExact` with the translated
layout, falling back to the existing general parse. Add tests covering the high-frequency
layouts listed in the inventory.

### 6.2 `postProcess: feetToCm` and `lbToKg`

Add cases in `ApplyPostProcesses` (~line 2188) mirroring stash semantics:
- `feetToCm`: parse `5'9"`-style or `5 ft 9 in` height → centimeters (integer string).
- `lbToKg`: parse a pounds number → kilograms (integer string).
Match stash's rounding/parsing behavior (see upstream `pkg/scraper` reference if needed).

### 6.3 `postProcess: subScraper`

`subScraper` fetches the value as a URL and runs a nested selector map against the fetched
document, replacing the value with the sub-scrape result. Implement it in
`ApplyPostProcesses`, reusing the existing XPath fetch + extract code paths. This is more
involved than 6.1/6.2 — only implement when a scraper you actually want needs it, and add a
focused test.

### 6.4 Anything else

If a scraper needs a feature not listed here (e.g. extra entry points, JSON path features,
cookie/header edge cases), decide: fix Cove (with a test) if it's broadly useful, or route
the scraper to §5 if it's a one-off. Record the call in the batch log.

---

## 7. Execution order (recommended)

1. **§3 cleanup + generator + catalog/build/registry changes** — land the tooling first.
2. **§6.1 parseDate fix** in Cove (unblocks correct dates for most scrapers).
3. **§6.2 feetToCm/lbToKg** (cheap, unlocks many performer scrapers).
4. **Batch through §4** pure-compatible scrapers (15–25 at a time), testing each.
5. **§6.3 subScraper** when the first scraper that needs it comes up; then port those.
6. **§5** code-based scrapers, shared API modules first, then the scrapers that use them.

---

## 8. Per-scraper Definition of Done

A scraper is "done" only when ALL hold:
- [ ] File(s) carry the AGPL-3.0 attribution header (§2) with the real upstream path.
- [ ] It lives in the correct place (`extensions/yaml/<id>` for YAML, `extensions/<Name>Scraper`
      for .NET) with a generated/valid `extension.json`.
- [ ] `npm run validate:extensions` passes.
- [ ] It is registered (catalog + registry) per the chosen §3.4/§3.5 strategy.
- [ ] It was **tested** against a real sample URL and returned sane fields, OR is explicitly
      marked `unverified` with the reason.
- [ ] Any Cove engine change it required has a test and Cove still builds.

---

## 9. Tracking & batching

Maintain a running log at `docs/porting-progress.md` (create it). For each scraper record:

| Upstream file | Target | Type (yaml/mixed/dotnet) | Status (done/unverified/blocked) | Sample URL tested | Cove fix needed | Notes |

Work in batches; commit per batch with a message like
`port: yaml scrapers batch N (<count> scrapers)`. Keep batches small enough that every
scraper in the batch was actually exercised.

---

## 10. Guardrails

- **Do not** bulk-copy all 638 files blindly; untested scrapers are worse than absent ones.
- **Do not** hand-write per-scraper `extension.json` / catalog / registry entries once the
  generator (§3.3) exists.
- **Do not** strip upstream attribution; **do** add the new header.
- **Do not** invent network permissions broader than the scraper needs (avoid `"*"` unless
  the hosts genuinely can't be derived).
- **Do** prefer fixing Cove's engine over forking a scraper's logic when the gap is general.
- **Do** rebuild `Cove.Api` after engine changes before re-testing (stale binaries lie).
- `communityscrapers/LICENSE` is confirmed AGPL-3.0, so upstream content may be copied; do
  not relicense the repo away from AGPL-3.0.
```

