#!/usr/bin/env node
// Generate officialextensionregistry entries for the source-tracked YAML scraper packs.
//
// YAML scrapers are unversioned and served directly from this repo's source tree.
// The registry entry therefore omits `versions[]`: Cove recognizes a source-tracked
// scraper-pack (kind=scraper-pack + sourceManifestUrl + no versions) and installs by
// fetching the manifest and its `scraperFiles` directly from raw GitHub. No release
// zips, checksums, or CI are involved.
//
// Usage:
//   node scripts/generate-registry-entries.mjs              # dry-run, prints planned changes
//   node scripts/generate-registry-entries.mjs --write      # write into ../officialextensionregistry
//   node scripts/generate-registry-entries.mjs --registry <path> --write

import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const yamlRoot = path.join(root, "extensions", "yaml");

const REPO_OWNER = "yourcove";
const REPO_NAME = "communityscrapers";
const BRANCH = "main";
const REPO_URL = `https://github.com/${REPO_OWNER}/${REPO_NAME}`;

function parseArgs(argv) {
  const opts = { write: false, registry: path.resolve(root, "..", "officialextensionregistry") };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--write") opts.write = true;
    else if (argv[i] === "--registry") opts.registry = path.resolve(argv[++i]);
  }
  return opts;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

function sourceManifestUrl(id) {
  return `https://raw.githubusercontent.com/${REPO_OWNER}/${REPO_NAME}/${BRANCH}/extensions/yaml/${id}/extension.json`;
}

function buildEntry(folderId, manifest) {
  const entry = {
    id: manifest.id,
    sourceManifestUrl: sourceManifestUrl(folderId),
    name: manifest.name,
    description: manifest.description,
    repositoryUrl: REPO_URL,
    kind: "scraper-pack",
    categories: manifest.categories ?? ["scraper", "metadata", "yaml-scraper"],
  };

  if (typeof manifest.author === "string" && manifest.author.trim().length > 0) {
    entry.author = manifest.author.trim();
  }

  return entry;
}

function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!fs.existsSync(yamlRoot)) {
    console.error(`No YAML scrapers found at ${yamlRoot}.`);
    process.exit(0);
  }

  const folders = fs.readdirSync(yamlRoot, { withFileTypes: true })
    .filter(d => d.isDirectory())
    .map(d => d.name)
    .sort();

  const entries = [];
  for (const folder of folders) {
    const manifestPath = path.join(yamlRoot, folder, "extension.json");
    if (!fs.existsSync(manifestPath)) continue;
    const manifest = readJson(manifestPath);
    entries.push({ folder, manifest, entry: buildEntry(folder, manifest) });
  }

  if (entries.length === 0) {
    console.log("No YAML scraper manifests found.");
    return;
  }

  const registryDir = opts.registry;
  const indexPath = path.join(registryDir, "index.json");
  const extensionsDir = path.join(registryDir, "extensions");

  if (!opts.write) {
    console.log(`Dry run (use --write to apply). Registry: ${registryDir}`);
    for (const { entry } of entries) {
      console.log(`  ${entry.id} -> extensions/${entry.id}.json (source-tracked, no versions)`);
    }
    console.log(`\n${entries.length} entr${entries.length === 1 ? "y" : "ies"} planned.`);
    return;
  }

  if (!fs.existsSync(registryDir)) {
    console.error(`ERROR: registry path does not exist: ${registryDir}`);
    process.exit(1);
  }
  fs.mkdirSync(extensionsDir, { recursive: true });

  const index = fs.existsSync(indexPath)
    ? readJson(indexPath)
    : { schemaVersion: "2.0", extensions: [] };
  const indexIds = new Set((index.extensions ?? []).map(e => e.id));

  for (const { entry } of entries) {
    fs.writeFileSync(path.join(extensionsDir, `${entry.id}.json`), JSON.stringify(entry, null, 2) + "\n", "utf8");
    if (!indexIds.has(entry.id)) {
      index.extensions.push({ id: entry.id });
      indexIds.add(entry.id);
    }
  }

  index.extensions.sort((a, b) => a.id.localeCompare(b.id));
  fs.writeFileSync(indexPath, JSON.stringify(index, null, 2) + "\n", "utf8");

  console.log(`Wrote ${entries.length} registry entr${entries.length === 1 ? "y" : "ies"} to ${registryDir}.`);
}

main();
