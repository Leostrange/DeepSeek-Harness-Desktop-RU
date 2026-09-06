#!/usr/bin/env node
/**
 * patch-browse-picker.mjs
 *
 * On Windows, the upstream @deepseek-ai/dsh resolves the directory-picker
 * backend to "native" (Win32 COM dialog via koffi).  When koffi or the
 * COM call fails, the host.pickDirectory RPC never returns and the
 * browser sees "Failed to fetch".
 *
 * This patch forces the "browse" backend (in-app file browser) on Windows,
 * which works everywhere without native dependencies.
 *
 * Usage:
 *   node patch-browse-picker.mjs [--base="<path>"]
 *
 * --base  root directory containing @deepseek-ai packages
 *         (default: auto-detect from the script's install root)
 */
import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";

const args = process.argv.slice(2);
let base = null;
for (const arg of args) {
  const m = arg.match(/^--base="?(.+?)"?$/);
  if (m) base = m[1];
}

if (!base) {
  // Walk up from the script location to find the install root
  const installRoot = resolve(import.meta.dirname, "..", "..");
  base = join(installRoot, "harness", "node_modules", "@deepseek-ai", "dsh", "node_modules", "@deepseek-ai");
}

const target = join(base, "dsh-host-directory-picker-auto", "lib", "index.js");

if (!existsSync(target)) {
  // Try the backup copy as well
  const altBase = base.replace(
    /dsh\/node_modules/,
    "dsh.bak/node_modules"
  );
  const alt = join(altBase, "dsh-host-directory-picker-auto", "lib", "index.js");
  if (existsSync(alt)) patchFile(alt);
  process.exit(0);
}

patchFile(target);

function patchFile(path) {
  let src = readFileSync(path, "utf8");

  // The upstream line:
  //   if (facts.platform === "darwin" || facts.platform === "win32") return "native";
  // Replace with:
  //   if (facts.platform === "darwin") return "native";
  // (keeps macOS native picker, forces Windows to browse)
  const before = 'if (facts.platform === "darwin" || facts.platform === "win32") return "native";';
  const after  = 'if (facts.platform === "darwin") return "native";';

  if (src.includes(before)) {
    src = src.replace(before, after);
    writeFileSync(path, src, "utf8");
    console.log(`[patch-browse-picker] patched ${path}`);
  } else if (src.includes(after)) {
    console.log(`[patch-browse-picker] already patched ${path}`);
  } else {
    console.warn(`[patch-browse-picker] target pattern not found in ${path}`);
  }
}
