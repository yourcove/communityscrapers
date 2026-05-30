#!/usr/bin/env node
// Generate a Cove YAML scraper extension from an upstream stashapp/CommunityScrapers .yml file.
//
// Usage:
//   node scripts/generate-yaml-scraper.mjs <upstream.yml> [<upstream2.yml> ...]
//   node scripts/generate-yaml-scraper.mjs --from-dir <dir>        # all top-level *.yml in <dir>
//   node scripts/generate-yaml-scraper.mjs <upstream.yml> --id custom-id
//   node scripts/generate-yaml-scraper.mjs <upstream.yml> --force  # overwrite + allow script actions
//
// Each upstream file becomes:
//   extensions/yaml/<id>/extension.json
//   extensions/yaml/<id>/scrapers/<id>.yml   (with AGPL-3.0 attribution header)
//
// YAML scrapers are intentionally unversioned and content-only: Cove installs them
// directly from source via the registry's source-tracked scraper-pack mechanism, so
// they need no release zips, checksums, or CI.

import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const yamlRoot = path.join(root, "extensions", "yaml");

const ID_PREFIX = "cove.community.scrapers.yaml.";
const UPSTREAM_TREE = "https://github.com/stashapp/CommunityScrapers/tree/master";
const SUPPORTED_POST_PROCESS_STEPS = new Set(["replace", "parsedate", "map", "feettocm", "lbtokg"]);

function parseArgs(argv) {
  const files = [];
  const opts = { id: null, force: false, fromDir: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--id") opts.id = argv[++i];
    else if (arg === "--force") opts.force = true;
    else if (arg === "--from-dir") opts.fromDir = argv[++i];
    else files.push(arg);
  }
  return { files, opts };
}

function toKebab(value) {
  return value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .replace(/-{2,}/g, "-");
}

function readNameFromYaml(content) {
  const match = content.match(/^name:\s*(.+?)\s*$/m);
  return match ? match[1].replace(/^['"]|['"]$/g, "").trim() : null;
}

// Best-effort host extraction from `url:` lists and `queryURL:` values.
function extractHosts(content) {
  const tokens = new Set();
  const lines = content.split(/\r?\n/);
  let inUrlList = false;
  let urlIndent = 0;

  for (const rawLine of lines) {
    const line = rawLine.replace(/#.*$/, "");
    const trimmed = line.trim();
    const indent = line.length - line.trimStart().length;

    const queryUrl = trimmed.match(/^queryURL:\s*(\S+)/);
    if (queryUrl) tokens.add(queryUrl[1]);

    if (/^url:\s*$/.test(trimmed)) {
      inUrlList = true;
      urlIndent = indent;
      continue;
    }

    if (inUrlList) {
      const item = trimmed.match(/^-\s*(.+?)\s*$/);
      if (item && indent > urlIndent) {
        tokens.add(item[1].replace(/^['"]|['"]$/g, ""));
        continue;
      }
      if (trimmed.length > 0) inUrlList = false;
    }
  }

  const hosts = new Set();
  for (const token of tokens) {
    let host = token.replace(/^https?:\/\//i, "").split(/[/?#{]/)[0].trim();
    if (!host || !host.includes(".")) continue;
    host = host.toLowerCase().replace(/^\.+|\.+$/g, "");
    if (!/^[a-z0-9.-]+$/.test(host)) continue;
    hosts.add(host);
    const labels = host.split(".");
    if (labels.length >= 2) hosts.add(`*.${labels.slice(-2).join(".")}`);
  }

  return [...hosts].sort();
}

function hasScriptAction(content) {
  return /^\s*-?\s*action:\s*script\s*$/m.test(content)
    || /^\s*action:\s*script\s*$/m.test(content)
    || /^\s*-?\s*action:\s*stash\s*$/m.test(content);
}

function hasUnsupportedSubScraper(content) {
  return /^\s*-?\s*subScraper:\s*\S+/m.test(content);
}

function findUnsupportedPostProcessSteps(content) {
  const unsupported = new Set();
  const lines = content.split(/\r?\n/);
  let inPostProcess = false;
  let postProcessIndent = 0;
  let stepIndent = null;

  for (const rawLine of lines) {
    const line = rawLine.replace(/#.*$/, "");
    const trimmed = line.trim();
    const indent = line.length - line.trimStart().length;

    if (!inPostProcess) {
      if (/^postProcess:\s*$/.test(trimmed)) {
        inPostProcess = true;
        postProcessIndent = indent;
        stepIndent = null;
      }
      continue;
    }

    if (trimmed.length === 0) continue;
    if (indent <= postProcessIndent) {
      inPostProcess = false;
      stepIndent = null;
      if (/^postProcess:\s*$/.test(trimmed)) {
        inPostProcess = true;
        postProcessIndent = indent;
      }
      continue;
    }

    const item = trimmed.match(/^-\s*([A-Za-z][A-Za-z0-9_]*)\s*:/);
    if (!item) continue;
    if (stepIndent == null) stepIndent = indent;
    if (indent !== stepIndent) continue;

    const step = item[1].toLowerCase();
    if (!SUPPORTED_POST_PROCESS_STEPS.has(step)) unsupported.add(item[1]);
  }

  return [...unsupported].sort((a, b) => a.localeCompare(b));
}

function buildHeader(upstreamRelative) {
  return [
    `# Ported from stashapp/CommunityScrapers (${UPSTREAM_TREE})`,
    `# Licensed under AGPL-3.0. This file was modified/ported for use with Cove.`,
    `# Upstream file: ${upstreamRelative}`,
    "",
  ].join("\n");
}

function resolveOutputId(upstreamPath, name, opts) {
  if (opts.id) return opts.id;

  const preferredId = toKebab(name);
  const preferredManifest = path.join(yamlRoot, preferredId, "extension.json");
  if (!fs.existsSync(preferredManifest)) return preferredId;

  const fallbackId = toKebab(path.basename(upstreamPath, path.extname(upstreamPath)));
  if (fallbackId !== preferredId) {
    const fallbackManifest = path.join(yamlRoot, fallbackId, "extension.json");
    if (!fs.existsSync(fallbackManifest)) {
      console.warn(`NOTE: ${path.basename(upstreamPath)} name-derived id ${preferredId} already exists; using filename-derived id ${fallbackId}.`);
      return fallbackId;
    }
  }

  return preferredId;
}

function generate(upstreamPath, opts) {
  if (!fs.existsSync(upstreamPath)) {
    console.error(`ERROR: upstream file not found: ${upstreamPath}`);
    return false;
  }

  const content = fs.readFileSync(upstreamPath, "utf8").replace(/^\uFEFF/, "");
  const name = readNameFromYaml(content);
  if (!name) {
    console.error(`ERROR: no top-level "name:" found in ${upstreamPath}`);
    return false;
  }

  if (hasScriptAction(content) && !opts.force) {
    console.warn(`SKIP: ${path.basename(upstreamPath)} uses action: script/stash and must be ported to .NET (see plan section 5). Use --force to emit anyway.`);
    return false;
  }

  if (hasUnsupportedSubScraper(content) && !opts.force) {
    console.warn(`SKIP: ${path.basename(upstreamPath)} uses subScraper, which Cove does not support yet. Route it to a later engine pass or use --force to emit anyway.`);
    return false;
  }

  const unsupportedPostProcesses = findUnsupportedPostProcessSteps(content);
  if (unsupportedPostProcesses.length > 0 && !opts.force) {
    console.warn(`SKIP: ${path.basename(upstreamPath)} uses unsupported postProcess step(s): ${unsupportedPostProcesses.join(", ")}. Route it to a later engine pass or use --force to emit anyway.`);
    return false;
  }

  const id = resolveOutputId(upstreamPath, name, opts);
  const extensionId = `${ID_PREFIX}${id}`;
  const extensionDir = path.join(yamlRoot, id);
  const scrapersDir = path.join(extensionDir, "scrapers");
  const ymlDest = path.join(scrapersDir, `${id}.yml`);
  const manifestDest = path.join(extensionDir, "extension.json");

  if (fs.existsSync(manifestDest) && !opts.force) {
    console.error(`ERROR: ${id} already exists. Use --force to overwrite.`);
    return false;
  }

  const upstreamRelative = `scrapers/${path.basename(upstreamPath)}`;
  const hosts = extractHosts(content);

  const manifest = {
    id: extensionId,
    name,
    version: "1.0.0",
    description: `YAML metadata scraper for ${name}.`,
    url: "https://github.com/yourcove/communityscrapers",
    kind: "scraper-pack",
    minCoveVersion: "0.0.16",
    categories: ["scraper", "metadata", "yaml-scraper"],
    scraperFiles: [`scrapers/${id}.yml`],
  };
  if (hosts.length > 0) manifest.permissions = { network: hosts };

  fs.mkdirSync(scrapersDir, { recursive: true });
  fs.writeFileSync(ymlDest, buildHeader(upstreamRelative) + content.replace(/^\uFEFF/, ""), "utf8");
  fs.writeFileSync(manifestDest, JSON.stringify(manifest, null, 2) + "\n", "utf8");

  console.log(`Generated ${path.relative(root, extensionDir)} (id=${extensionId}${hosts.length ? `, hosts=${hosts.length}` : ", no hosts detected"})`);
  if (hosts.length === 0) console.warn(`  NOTE: no network hosts detected; add permissions.network manually if required.`);
  return true;
}

function main() {
  const { files, opts } = parseArgs(process.argv.slice(2));

  let targets = files;
  if (opts.fromDir) {
    const dir = path.resolve(opts.fromDir);
    targets = fs.readdirSync(dir)
      .filter(f => [".yml", ".yaml"].includes(path.extname(f).toLowerCase()))
      .map(f => path.join(dir, f));
  }

  if (targets.length === 0) {
    console.error("Usage: node scripts/generate-yaml-scraper.mjs <upstream.yml> [...] | --from-dir <dir>");
    process.exit(1);
  }

  if (opts.id && targets.length > 1) {
    console.error("ERROR: --id can only be used with a single upstream file.");
    process.exit(1);
  }

  let ok = 0;
  let skipped = 0;
  for (const target of targets) {
    if (generate(target, opts)) ok++;
    else skipped++;
  }

  console.log(`\nDone. Generated ${ok}, skipped ${skipped}.`);
}

main();
