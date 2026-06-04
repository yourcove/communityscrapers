import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");
const buildPropsPath = path.join(root, "Directory.Build.props");
const errors = [];
const SUPPORTED_POST_PROCESS_STEPS = new Set(["replace", "parsedate", "map", "feettocm", "lbtokg"]);

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

function isLowerKebab(value) {
  return value === value.toLowerCase() && !value.includes(" ");
}

function readMsBuildProperties(filePath) {
  if (!fs.existsSync(filePath)) return {};

  const props = {};
  const content = fs.readFileSync(filePath, "utf8");
  const pattern = /<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s+[^>]*)?>([^<]*)<\/\1>/g;
  for (const match of content.matchAll(pattern)) {
    const [, name, rawValue] = match;
    const value = rawValue.trim().replace(/\$\(([^)]+)\)/g, (_, propertyName) => props[propertyName] ?? `$(${propertyName})`);
    props[name] = value;
  }

  return props;
}

function parseVersion(value) {
  if (typeof value !== "string") return null;
  const match = value.match(/^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/);
  if (!match) return null;
  return match.slice(1).map(part => Number.parseInt(part, 10));
}

function compareVersions(left, right) {
  const leftParts = parseVersion(left);
  const rightParts = parseVersion(right);
  if (!leftParts || !rightParts) return null;

  for (let i = 0; i < 3; i++) {
    if (leftParts[i] !== rightParts[i]) return leftParts[i] - rightParts[i];
  }

  return 0;
}

function validateVersionFloor(label, field, value, minimum) {
  if (!value) {
    errors.push(`${label}: ${field} is missing`);
    return;
  }

  const comparison = compareVersions(value, minimum);
  if (comparison == null) {
    errors.push(`${label}: ${field} must be a semantic version, found ${value}`);
  } else if (comparison < 0) {
    errors.push(`${label}: ${field} ${value} is below repo CoveMinVersion ${minimum}`);
  }
}

function validateExternalDependencies(extensionId, manifest) {
  if (manifest.externalDependencies == null) return;
  if (!Array.isArray(manifest.externalDependencies)) {
    errors.push(`${extensionId}: extension.json externalDependencies must be an array`);
    return;
  }

  for (const dependency of manifest.externalDependencies) {
    if (!dependency?.id) errors.push(`${extensionId}: external dependency missing id`);
    if (!dependency?.name) errors.push(`${extensionId}: external dependency missing name`);
    if (Object.prototype.hasOwnProperty.call(dependency, "optional")) {
      errors.push(`${extensionId}: external dependency uses legacy optional; use required`);
    }
    if (Object.prototype.hasOwnProperty.call(dependency, "settingsKey")) {
      errors.push(`${extensionId}: external dependency uses legacy settingsKey; use configurationKeys`);
    }
    if (dependency.configurationKeys != null && !Array.isArray(dependency.configurationKeys)) {
      errors.push(`${extensionId}: external dependency configurationKeys must be an array`);
    }
  }
}

function validateSettings(extensionId, manifest) {
  if (manifest.settings == null) return;
  if (!Array.isArray(manifest.settings)) {
    errors.push(`${extensionId}: extension.json settings must be an array`);
    return;
  }

  for (const setting of manifest.settings) {
    if (!setting?.name) errors.push(`${extensionId}: setting missing name`);
    if (Object.prototype.hasOwnProperty.call(setting, "key")) {
      errors.push(`${extensionId}: setting uses legacy key; use name`);
    }
    if (Object.prototype.hasOwnProperty.call(setting, "label")) {
      errors.push(`${extensionId}: setting uses legacy label; use displayName`);
    }
    if (Object.prototype.hasOwnProperty.call(setting, "defaultValue")) {
      errors.push(`${extensionId}: setting uses legacy defaultValue; remove it from extension.json`);
    }
    if (Object.prototype.hasOwnProperty.call(setting, "scope")) {
      errors.push(`${extensionId}: setting uses legacy scope; remove it from extension.json`);
    }
  }
}

function findFiles(directory, predicate) {
  if (!fs.existsSync(directory)) return [];

  const results = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      results.push(...findFiles(fullPath, predicate));
      continue;
    }

    if (entry.isFile() && predicate(fullPath)) results.push(fullPath);
  }

  return results;
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

const catalog = readJson(catalogPath);
const entries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
const buildProps = readMsBuildProperties(buildPropsPath);
const coveMinVersion = buildProps.CoveMinVersion;

if (!catalog.schemaVersion) errors.push("extensions/catalog.json missing schemaVersion");
if (entries.length === 0) errors.push("extensions/catalog.json has no extensions");
if (!coveMinVersion) errors.push("Directory.Build.props missing CoveMinVersion");
if (coveMinVersion) {
  validateVersionFloor("Directory.Build.props", "CovePluginsVersion", buildProps.CovePluginsVersion, coveMinVersion);
  validateVersionFloor("Directory.Build.props", "CoveCoreVersion", buildProps.CoveCoreVersion, coveMinVersion);
}

const ids = new Set();
const tagPrefixes = new Set();
for (const entry of entries) {
  for (const field of ["name", "id", "path", "tagPrefix"]) {
    if (!entry[field]) errors.push(`${entry.id ?? entry.name ?? "catalog entry"}: missing ${field}`);
  }

  if (entry.id && ids.has(entry.id)) errors.push(`${entry.id}: duplicate extension id`);
  if (entry.id) ids.add(entry.id);

  if (entry.tagPrefix && tagPrefixes.has(entry.tagPrefix)) errors.push(`${entry.id}: duplicate tagPrefix ${entry.tagPrefix}`);
  if (entry.tagPrefix) tagPrefixes.add(entry.tagPrefix);
  if (entry.tagPrefix && !entry.tagPrefix.endsWith("/")) errors.push(`${entry.id}: tagPrefix must end with /`);

  const extensionDir = path.join(root, entry.path ?? "");
  const manifestPath = path.join(extensionDir, "extension.json");
  const projectPath = path.join(extensionDir, `${entry.name}.csproj`);
  const isManifestOnly = entry.manifestOnly === true;
  const isContentOnly = entry.contentOnly === true;

  if (!fs.existsSync(extensionDir)) {
    errors.push(`${entry.id}: path does not exist: ${entry.path}`);
    continue;
  }
  if (!fs.existsSync(manifestPath)) {
    errors.push(`${entry.id}: missing extension.json at ${entry.path}`);
    continue;
  }
  if (!isManifestOnly && !fs.existsSync(projectPath)) {
    errors.push(`${entry.id}: missing project ${entry.name}.csproj at ${entry.path}`);
  }

  const manifest = readJson(manifestPath);
  if (manifest.id !== entry.id) errors.push(`${entry.id}: catalog id does not match extension.json id ${manifest.id}`);
  if (!manifest.version) errors.push(`${entry.id}: extension.json missing version`);
  if (coveMinVersion) validateVersionFloor(entry.id, "extension.json minCoveVersion", manifest.minCoveVersion, coveMinVersion);
  if (!isManifestOnly && !manifest.entryDll) errors.push(`${entry.id}: extension.json missing entryDll`);
  if (isManifestOnly && manifest.entryDll) errors.push(`${entry.id}: manifestOnly entry must not declare entryDll`);
  if (isManifestOnly && !["bundle", "scraper-pack"].includes(manifest.kind)) {
    errors.push(`${entry.id}: manifestOnly entries must use kind=bundle or kind=scraper-pack`);
  }
  if (isContentOnly && !isManifestOnly) errors.push(`${entry.id}: contentOnly entries must also set manifestOnly=true`);
  if (isContentOnly && manifest.kind !== "scraper-pack") errors.push(`${entry.id}: contentOnly scraper entries must use kind=scraper-pack`);
  if (!isContentOnly && manifest.kind === "scraper-pack") errors.push(`${entry.id}: scraper-pack entries must set contentOnly=true`);
  if (isContentOnly) {
    const scrapersDir = path.join(extensionDir, "scrapers");
    if (!fs.existsSync(scrapersDir)) {
      errors.push(`${entry.id}: contentOnly scraper pack missing scrapers directory`);
    } else {
      const scraperFiles = findFiles(scrapersDir, file => [".yml", ".yaml"].includes(path.extname(file).toLowerCase()));
      if (scraperFiles.length === 0) errors.push(`${entry.id}: contentOnly scraper pack must include at least one .yml or .yaml file`);
    }
  }
  if (!manifest.url) errors.push(`${entry.id}: extension.json missing url`);
  if (!Array.isArray(manifest.categories) || manifest.categories.length === 0) {
    errors.push(`${entry.id}: extension.json missing categories`);
  } else {
    for (const category of manifest.categories) {
      if (!isLowerKebab(category)) errors.push(`${entry.id}: category must be lowercase kebab-case: ${category}`);
    }
  }

  validateExternalDependencies(entry.id, manifest);
  validateSettings(entry.id, manifest);
}

// Source-tracked YAML scraper packs live under extensions/yaml/<id>/ and are
// auto-discovered (not part of catalog.json). They are unversioned and content-only:
// Cove installs them directly from source, so they need no release zips or CI.
const YAML_ID_PREFIX = "cove.community.scrapers.yaml.";
const yamlScrapersRoot = path.join(root, "extensions", "yaml");

function validateYamlScraperPack(folder) {
  const extensionDir = path.join(yamlScrapersRoot, folder);
  const manifestPath = path.join(extensionDir, "extension.json");
  const label = `yaml/${folder}`;

  if (!fs.existsSync(manifestPath)) {
    errors.push(`${label}: missing extension.json`);
    return;
  }

  const manifest = readJson(manifestPath);
  const expectedId = `${YAML_ID_PREFIX}${folder}`;

  if (manifest.id !== expectedId) errors.push(`${label}: id must be ${expectedId} (found ${manifest.id})`);
  if (manifest.kind !== "scraper-pack") errors.push(`${label}: kind must be scraper-pack`);
  if (manifest.entryDll) errors.push(`${label}: YAML scraper packs must not declare entryDll`);
  if (coveMinVersion) validateVersionFloor(label, "extension.json minCoveVersion", manifest.minCoveVersion, coveMinVersion);
  if (!manifest.url) errors.push(`${label}: extension.json missing url`);
  if (!Array.isArray(manifest.categories) || manifest.categories.length === 0) {
    errors.push(`${label}: extension.json missing categories`);
  } else {
    for (const category of manifest.categories) {
      if (!isLowerKebab(category)) errors.push(`${label}: category must be lowercase kebab-case: ${category}`);
    }
  }

  if (!Array.isArray(manifest.scraperFiles) || manifest.scraperFiles.length === 0) {
    errors.push(`${label}: extension.json must list at least one scraperFiles entry`);
    return;
  }

  for (const relative of manifest.scraperFiles) {
    if (typeof relative !== "string" || relative.length === 0) {
      errors.push(`${label}: scraperFiles entries must be non-empty strings`);
      continue;
    }
    const normalized = relative.replace(/\\/g, "/");
    if (normalized.startsWith("/") || normalized.includes("..")) {
      errors.push(`${label}: scraperFiles entry must be a relative path inside the pack: ${relative}`);
      continue;
    }
    if (![".yml", ".yaml"].includes(path.extname(normalized).toLowerCase())) {
      errors.push(`${label}: scraperFiles entry must be a .yml or .yaml file: ${relative}`);
      continue;
    }
    const filePath = path.join(extensionDir, normalized);
    if (!fs.existsSync(filePath)) {
      errors.push(`${label}: scraperFiles references a missing file: ${relative}`);
      continue;
    }
    const content = fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, "");
    if (!content.includes("stashapp/CommunityScrapers")) {
      errors.push(`${label}: ${relative} is missing the required stashapp/CommunityScrapers attribution header`);
    }
    if (!/^name:\s*\S/m.test(content)) {
      errors.push(`${label}: ${relative} is missing a top-level name:`);
    }
    if (/^\s*-?\s*action:\s*(script|stash)\s*$/m.test(content)) {
      errors.push(`${label}: ${relative} uses action: script/stash; port it to a .NET scraper instead of a YAML pack`);
    }
    if (/^\s*-?\s*subScraper:\s*\S+/m.test(content)) {
      errors.push(`${label}: ${relative} uses subScraper; Cove does not support that in YAML packs yet`);
    }
    const unsupportedPostProcesses = findUnsupportedPostProcessSteps(content);
    if (unsupportedPostProcesses.length > 0) {
      errors.push(`${label}: ${relative} uses unsupported postProcess step(s): ${unsupportedPostProcesses.join(", ")}`);
    }
  }

  validateExternalDependencies(label, manifest);
  validateSettings(label, manifest);
}

let yamlPackCount = 0;
if (fs.existsSync(yamlScrapersRoot)) {
  const yamlFolders = fs.readdirSync(yamlScrapersRoot, { withFileTypes: true })
    .filter(entry => entry.isDirectory())
    .map(entry => entry.name);

  for (const folder of yamlFolders) {
    yamlPackCount++;
    const id = `${YAML_ID_PREFIX}${folder}`;
    if (ids.has(id)) errors.push(`yaml/${folder}: id ${id} duplicates a catalog entry`);
    ids.add(id);
    validateYamlScraperPack(folder);
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exit(1);
}

console.log(`Validated ${entries.length} extension catalog entries and ${yamlPackCount} YAML scraper pack(s).`);
