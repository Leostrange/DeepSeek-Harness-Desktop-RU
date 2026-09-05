#!/usr/bin/env node
/**
 * Apply (or revert) the Russian locale patch to the installed DeepSeek Harness
 * web UI. The patch edits `@deepseek-ai/dsh-client-locale/lib/client.js`:
 *   1. adds `ru` to the shipped LOCALES list,
 *   2. injects the Russian dictionaries (ru-dicts.json, ~30 namespaces),
 *   3. registers them in the locale plugin's `apply()`,
 * and additionally localises the hardcoded permission option labels
 * (Read Only / Workspace Write / Full access) in two UI bundles.
 *
 * Usage:
 *   node apply-ru.mjs            # apply (idempotent)
 *   node apply-ru.mjs --revert   # restore the pre-patch files
 *
 * Original files are kept at *.codebuff-ru.bak on first apply.
 */
import { readFileSync, writeFileSync, existsSync, copyFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const dicts = JSON.parse(readFileSync(path.join(here, 'ru-dicts.json'), 'utf8'));

const appdata = process.env.APPDATA;
if (!appdata) {
  console.error('APPDATA is not set — cannot locate the dsh install.');
  process.exit(1);
}
// `--base=<dir>` targets a bundled/offline copy (the @deepseek-ai modules root).
const baseArg = process.argv.find((a) => a.startsWith('--base='));
const npmRoot = baseArg
  ? path.resolve(baseArg.slice('--base='.length))
  : path.join(appdata, 'npm', 'node_modules', '@deepseek-ai', 'dsh', 'node_modules', '@deepseek-ai');
const target = path.join(npmRoot, 'dsh-client-locale', 'lib', 'client.js');
const backup = target + '.codebuff-ru.bak';

if (!existsSync(target)) {
  console.error('Target not found:', target);
  process.exit(1);
}

const src = readFileSync(target, 'utf8');
const REVERT = process.argv.includes('--revert');

// ---- dictionary block (module level, indented to factory body = 2 tabs) ----
function dictBlock() {
  const lines = [];
  lines.push('\t\t/* == codebuff-ru:i18n start == */');
  lines.push('\t\t/** Russian dictionaries (community translation). */');
  lines.push('\t\tconst RU_DICTS = {');
  for (const [ns, d] of Object.entries(dicts)) {
    lines.push(`\t\t\t${JSON.stringify(ns)}: {`);
    for (const [k, v] of Object.entries(d)) lines.push(`\t\t\t\t${JSON.stringify(k)}: ${JSON.stringify(v)},`);
    lines.push('\t\t\t},');
  }
  lines.push('\t\t};');
  lines.push('\t\t/* == codebuff-ru:i18n end == */');
  return lines.join('\n');
}

const REG_LOOP =
  '\t\t\t/* == codebuff-ru:register start == */\n' +
  '\t\t\tfor (const [ns, dict] of Object.entries(RU_DICTS)) locale.register(ns, "ru", dict);\n' +
  '\t\t\t/* == codebuff-ru:register end == */';

const LOCALES_OLD = `\t\t/** The two shipped locales. */
\t\tconst LOCALES = Object.freeze([{
\t\t\tid: "zh",
\t\t\tlabel: "中文"
\t\t}, {
\t\t\tid: "en",
\t\t\tlabel: "English"
\t\t}]);`;

const LOCALES_NEW = `\t\t/** The three shipped locales. */
\t\tconst LOCALES = Object.freeze([{
\t\t\tid: "zh",
\t\t\tlabel: "中文"
\t\t}, {
\t\t\tid: "en",
\t\t\tlabel: "English"
\t\t}, {
\t\t\tid: "ru",
\t\t\tlabel: "Русский"
\t\t}]);`;

// ---- hardcoded permission option labels (Read Only / Workspace Write / Full access) ----
function permissionBundlePaths() {
  return [
    path.join(npmRoot, 'dsh-client-ui-permission-presets', 'lib', 'client.js'),
    path.join(npmRoot, 'dsh-client-ui-conversation', 'lib', 'client.js'),
  ];
}

function patchPermissionLabels() {
  const ruPermNames = 'const ruPermNames = { "read-only": "Только чтение", "workspace-write": "Запись в рабочей области" };';
  const [perm, conv] = permissionBundlePaths();
  let changed = 0;

  for (const p of [perm, conv]) {
    if (!existsSync(p)) continue;
    let s = readFileSync(p, 'utf8');
    if (s.includes('Только чтение')) continue; // already patched
    if (!existsSync(p + '.codebuff-ru.bak')) copyFileSync(p, p + '.codebuff-ru.bak');
    const fnName = p === perm ? 'displayPresetName' : 'displayName';
    const oldFn = `\t\tfunction ${fnName}(name) {
\t\t\tif (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(name)) return name;
\t\t\treturn name.split("-").map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(" ");
\t\t}`;
    const newFn = `\t\tfunction ${fnName}(name) {
\t\t\t${ruPermNames}
\t\t\tif (ruPermNames[name]) return ruPermNames[name];
\t\t\tif (!/^[a-z0-9]+(-[a-z0-9]+)*$/.test(name)) return name;
\t\t\treturn name.split("-").map((word) => word.charAt(0).toUpperCase() + word.slice(1)).join(" ");
\t\t}`;
    s = s.replace(oldFn, newFn);
    s = p === perm
      ? s.replace('value === "danger-full-access" ? "Full access" : displayPresetName(name)', 'value === "danger-full-access" ? "Полный доступ" : displayPresetName(name)')
      : s.replace('option.value === FULL_ACCESS ? "Full access" : displayName(option.name)', 'option.value === FULL_ACCESS ? "Полный доступ" : displayName(option.name)');
    writeFileSync(p, s, 'utf8');
    changed++;
  }
  console.log('Permission option labels localised in', changed, 'bundle(s).');
}

if (REVERT) {
  let reverted = false;
  if (existsSync(backup)) {
    copyFileSync(backup, target);
    console.log('Reverted:', target);
    reverted = true;
  }
  for (const p of permissionBundlePaths()) {
    const b = p + '.codebuff-ru.bak';
    if (existsSync(b)) {
      copyFileSync(b, p);
      console.log('Reverted:', p);
      reverted = true;
    }
  }
  console.log(reverted ? 'Reverted to pre-patch files.' : 'No backups found — nothing to revert.');
  process.exit(0);
}

if (!existsSync(backup)) copyFileSync(target, backup);

// Re-running refreshes the dictionaries: strip any previous patch blocks first.
let stripped = src;
stripped = stripped.replace(/\n?\t\t\/\* == codebuff-ru:i18n start == \*\/[\s\S]*?\/\* == codebuff-ru:i18n end == \*\//, '');
stripped = stripped.replace(/\n?\t\t\t\/\* == codebuff-ru:register start == \*\/[\s\S]*?\/\* == codebuff-ru:register end == \*\//, '');

let out = stripped;

// 1) LOCALES (skip when the ru entry is already present from an earlier run)
if (out.includes('id: "ru"')) {
  // already added
} else if (!out.includes(LOCALES_OLD)) {
  console.error('LOCALES block not found — dsh version may have changed. No changes written.');
  process.exit(1);
} else {
  out = out.replace(LOCALES_OLD, LOCALES_NEW);
}

// 2) dictionaries before `function apply(ctx) {`
const applyMarker = '\t\tfunction apply(ctx) {';
if (!out.includes(applyMarker)) {
  console.error('apply(ctx) not found — dsh version may have changed. No changes written.');
  process.exit(1);
}
out = out.replace(applyMarker, dictBlock() + '\n' + applyMarker);

// 3) registration loop right after the locale service is provided
const provideMarker = '\t\t\tctx.provide("locale", locale);';
if (!out.includes(provideMarker)) {
  console.error('ctx.provide("locale", locale) not found — dsh version may have changed. No changes written.');
  process.exit(1);
}
out = out.replace(provideMarker, provideMarker + '\n' + REG_LOOP);

writeFileSync(target, out, 'utf8');
console.log('Patched:', target);
console.log('  LOCALES now includes ru;', Object.keys(dicts).length, 'namespaces registered.');

patchPermissionLabels();
