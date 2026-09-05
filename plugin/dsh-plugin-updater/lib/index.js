/**
 * @deepseek-ai/dsh-plugin-updater — in-app updater for DeepSeek Harness.
 *
 * Registers HTTP routes on the harness webServer:
 *   GET  /api/updater/status  → versions + update state
 *   POST /api/updater/check   → force re-check
 *   POST /api/updater/apply   → download + swap dsh files in background
 *   GET  /updater             → settings UI page
 *
 * Update source: official GitHub releases (deepseek-ai/deepseek-harness),
 * with automatic fallback to the npm registry when no release assets exist.
 *
 * Only lib/, config/ and package.json of the dsh package itself are swapped —
 * the shell (оболочка), node_modules and user data are never touched.
 */
import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync, cpSync, createWriteStream } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { execFile } from "node:child_process";
import { tmpdir } from "node:os";
import https from "node:https";

const __dirname = dirname(fileURLToPath(import.meta.url));
const GH_REPO = "deepseek-ai/deepseek-harness";
const NPM_META = "https://registry.npmjs.org/@deepseek-ai/dsh/latest";
const UA = "dsh-plugin-updater/1.1";

// ── Locate the dsh package root ──────────────────────────────────────────
// Works in every shipped layout. The plugin lives in a stable shell-owned
// home (harness/extra/), so while walking up we probe known locations:
// <a>/node_modules/@deepseek-ai/dsh (Windows layout),
// <a>/harness/node_modules/@deepseek-ai/dsh, and
// <a>/harness/package (Linux offline layout where dsh IS the root package).
function isDshPkg(pkgPath) {
  try {
    return JSON.parse(readFileSync(pkgPath, "utf-8")).name === "@deepseek-ai/dsh";
  } catch { return false; }
}
function findDshRoot() {
  // 1. The shell tells us explicitly (set by DshApi.cs / deepseek-harness.sh).
  //    This is the only reliable source when this plugin runs from its wiring
  //    copy inside $DSH_HOME/profiles/web/node_modules — walk-up cannot reach
  //    the bundle from there.
  const envDir = process.env.DSH_BUNDLE_DIR;
  if (envDir && isDshPkg(join(envDir, "package.json"))) return envDir;
  // 2. Walk-up (plugin living in harness/extra or inside the bundle itself).
  let dir = __dirname;
  for (let i = 0; i < 10; i++) {
    const candidates = [
      join(dir, "package.json"),
      join(dir, "node_modules", "@deepseek-ai", "dsh", "package.json"),
      join(dir, "harness", "node_modules", "@deepseek-ai", "dsh", "package.json"),
      join(dir, "harness", "package", "package.json"),
      join(dir, "package", "package.json"),
    ];
    for (const c of candidates) {
      // CRITICAL: never accept the profile's pnpm cache copy
      // ($DSH_HOME/profiles/node_modules/@deepseek-ai/dsh) — updates must
      // target the shell's bundle, not the profile dependency tree.
      if (!/[/\\]profiles[/\\]/.test(c) && existsSync(c) && isDshPkg(c)) return dirname(c);
    }
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  return null;
}
const DSH_ROOT = findDshRoot();

function log(msg) {
  try {
    const logDir = process.env.DSH_HOME
      ? join(process.env.DSH_HOME, "logs")
      : join(tmpdir(), "deepseek-harness-logs");
    mkdirSync(logDir, { recursive: true });
    const ts = new Date().toISOString().replace("T", " ").slice(0, 19);
    writeFileSync(join(logDir, "updater.log"), `${ts} ${msg}\n`, { flag: "a" });
  } catch { /* logging must never break the app */ }
}

function installedVersion() {
  if (!DSH_ROOT) return null;
  try {
    return JSON.parse(readFileSync(join(DSH_ROOT, "package.json"), "utf-8")).version ?? null;
  } catch { return null; }
}

// ── Network helpers ──────────────────────────────────────────────────────
function getJSON(url, redirects = 3) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { "User-Agent": UA, Accept: "application/json" } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        return resolve(getJSON(new URL(res.headers.location, url).toString(), redirects - 1));
      }
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error(`HTTP ${res.statusCode} for ${url}`));
      }
      let data = "";
      res.on("data", (c) => { data += c; });
      res.on("end", () => {
        try { resolve(JSON.parse(data)); } catch (e) { reject(e); }
      });
    }).on("error", reject);
  });
}

function download(url, dest, redirects = 5) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { "User-Agent": UA } }, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location && redirects > 0) {
        res.resume();
        return resolve(download(new URL(res.headers.location, url).toString(), dest, redirects - 1));
      }
      if (res.statusCode !== 200) {
        res.resume();
        return reject(new Error(`HTTP ${res.statusCode} for ${url}`));
      }
      const ws = createWriteStream(dest);
      res.pipe(ws);
      ws.on("finish", () => ws.close(() => resolve(dest)));
      ws.on("error", reject);
    }).on("error", reject);
  });
}

// ── Update check: GitHub releases first, npm registry as fallback ────────
async function checkForUpdate() {
  const installed = installedVersion();
  let latest = null;
  let changelog = "";
  let downloadUrl = null;
  let source = null;
  try {
    const rel = await getJSON(`https://api.github.com/repos/${GH_REPO}/releases/latest`);
    latest = (rel.tag_name || rel.name || "").replace(/^v/, "") || null;
    changelog = rel.body || "";
    const asset = (rel.assets || []).find((a) => /\.t(gz|ar\.gz)$/.test(a.name));
    if (asset) downloadUrl = asset.browser_download_url;
    if (latest && downloadUrl) source = "github";
  } catch (e) {
    log(`GitHub release check failed (${e.message}); falling back to npm`);
  }
  if (!latest || !downloadUrl) {
    // No usable GitHub release — use the official npm distribution.
    const meta = await getJSON(NPM_META);
    latest = latest || meta.version;
    downloadUrl = meta.dist?.tarball || `https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-${latest}.tgz`;
    source = source || "npm";
  }
  return { installed, latest, changelog, downloadUrl, source };
}

// ── Apply: swap only dsh-owned files (lib/, config/, package.json) ───────
let updating = false;
let lastResult = null;

async function applyUpdate(url, version) {
  log(`applying update to ${version} from ${url}`);
  const tgz = join(tmpdir(), `dsh-${version}.tgz`);
  const stage = join(tmpdir(), `dsh-update-${version}`);
  rmSync(stage, { recursive: true, force: true });
  mkdirSync(stage, { recursive: true });
  try {
    await download(url, tgz);
    log("downloaded");

    await new Promise((resolve, reject) => {
      // Windows: System32 ships bsdtar which handles C:\ paths; a GNU tar
      // from PATH parses "C:\..." as a remote host ("Cannot connect to C:").
      // Linux/macOS: tar is standard.
      const tarExe = process.platform === "win32"
        ? join(process.env.SystemRoot || "C:\\Windows", "System32", "tar.exe")
        : "tar";
      execFile(tarExe, ["-xzf", tgz, "-C", stage], { timeout: 120000 },
        (err) => (err ? reject(err) : resolve()));
    });
    log("extracted");

    const src = join(stage, "package");
    if (!existsSync(join(src, "lib"))) throw new Error("tarball has no lib/ directory");

    const changed = depsChanged(join(DSH_ROOT, "package.json"), join(src, "package.json"));
    if (!changed) {
      // Fast path: dependencies unchanged → swap only dsh-owned files.
      for (const part of ["lib", "config"]) {
        const from = join(src, part);
        const to = join(DSH_ROOT, part);
        if (existsSync(from)) {
          rmSync(to, { recursive: true, force: true });
          cpSync(from, to, { recursive: true });
        }
      }
      const pkgSrc = join(src, "package.json");
      if (existsSync(pkgSrc)) {
        writeFileSync(join(DSH_ROOT, "package.json"), readFileSync(pkgSrc));
      }
      log(`updated to ${version} (lib/config/package.json swapped, node_modules untouched)`);
    } else {
      // Dependencies changed → fresh node_modules required. First a dry-run
      // pre-flight: official releases sometimes ship with unpublished deps
      // (e.g. 0.1.2-rc.1 → dsh-experimental-agent-team 404). Such releases
      // are rejected before touching the running installation.
      const npmCli = npmCliCandidates().find((p) => existsSync(p));
      if (!npmCli) throw new Error("npm-cli.js not found next to node executable");
      log("pre-flight: npm install --dry-run (resolve-only)");
      await new Promise((resolve, reject) => {
        execFile(process.execPath,
          [npmCli, "install", "--dry-run", "--ignore-scripts", "--no-audit", "--no-fund", "--no-progress", "--prefer-offline"],
          { cwd: src, timeout: 240000 },
          (err, stdout, stderr) => {
            if (err) {
              const miss = String(stderr || stdout || "").split("\n")
                .find((l) => l.includes("E404") || l.includes("could not be found"));
              reject(new Error(miss
                ? `релиз повреждён: ${miss.trim()} — пакета нет в npm`
                : "зависимости релиза не разрешаются в npm"));
            } else resolve();
          });
      });
      log("pre-flight passed — running npm install in staging");
      await new Promise((resolve, reject) => {
        execFile(process.execPath,
          [npmCli, "install", "--omit=dev", "--no-audit", "--no-fund", "--no-progress", "--prefer-offline"],
          { cwd: src, timeout: 600000 },
          (err) => (err ? reject(err) : resolve()));
      });
      log("npm install done — replacing whole bundle");
      const backup = DSH_ROOT + ".bak";
      rmSync(backup, { recursive: true, force: true });
      try {
        renameSync(DSH_ROOT, backup);
        renameSync(src, DSH_ROOT);
      } catch (e) {
        // On Windows the running harness may hold the bundle directory.
        // The shell's own updater (update bar) stops the server first —
        // point the user there instead of leaving a half-swapped state.
        log(`full swap failed under running process: ${e.message}`);
        throw new Error("не удалось заменить бандл под запущенным процессом — закройте приложение и обновите через панель оболочки");
      }
      // Rotate the profile's cached node_modules: it holds the previous
      // release's bundle versions and stale copies cause version skew
      // (broken settings, false offline banners). dsh re-prepares on boot.
      const profNm = process.env.DSH_HOME && join(process.env.DSH_HOME, "profiles", "node_modules");
      if (profNm && existsSync(profNm)) {
        const profBak = profNm + ".pre-update";
        rmSync(profBak, { recursive: true, force: true });
        renameSync(profNm, profBak);
        log("profile node_modules rotated (will re-prepare on next boot)");
      }
      log(`updated to ${version} (full bundle replace with node_modules, backup at .bak)`);
    }
  } finally {
    rmSync(tgz, { force: true });
    rmSync(stage, { recursive: true, force: true });
  }
}

// npm's cli.js lives beside the running node executable in our distributions.
function npmCliCandidates() {
  const bin = dirname(process.execPath);
  return [
    join(bin, "node_modules", "npm", "bin", "npm-cli.js"),                 // Windows layout
    join(bin, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),    // Linux node distro
  ];
}

function depsChanged(oldPkg, newPkg) {
  try {
    const a = JSON.parse(readFileSync(oldPkg, "utf-8")).dependencies ?? {};
    const b = JSON.parse(readFileSync(newPkg, "utf-8")).dependencies ?? {};
    const ka = Object.keys(a), kb = Object.keys(b);
    if (ka.length !== kb.length) return true;
    return ka.some((k) => a[k] !== b[k]);
  } catch {
    return true; // unverifiable → treat as changed (safe path)
  }
}

// ── HTTP handlers ────────────────────────────────────────────────────────
function sendJSON(res, code, obj) {
  res.writeHead(code, { "Content-Type": "application/json", "Access-Control-Allow-Origin": "*" });
  res.end(JSON.stringify(obj));
}

function handleStatus(_req, res) {
  checkForUpdate()
    .then((info) => sendJSON(res, 200, { ...info, updating, lastResult }))
    .catch((e) => sendJSON(res, 500, { error: e.message }));
}

function handleCheck(_req, res) {
  checkForUpdate()
    .then((info) => sendJSON(res, 200, { ...info, updating, lastResult, checked: true }))
    .catch((e) => sendJSON(res, 500, { error: e.message }));
}

async function handleApply(_req, res) {
  try {
    if (!DSH_ROOT) return sendJSON(res, 500, { error: "dsh package root not found" });
    if (updating) return sendJSON(res, 409, { error: "update already in progress" });
    const info = await checkForUpdate();
    if (!info.latest) return sendJSON(res, 500, { error: "cannot determine latest version" });
    if (info.installed === info.latest) {
      return sendJSON(res, 200, { message: "Уже установлена актуальная версия", version: info.installed });
    }
    sendJSON(res, 202, { message: "Обновление запущено", target: info.latest });
    updating = true;
    applyUpdate(info.downloadUrl, info.latest)
      .then(() => { lastResult = { ok: true, version: info.latest }; })
      .catch((e) => { log(`update failed: ${e.message}`); lastResult = { ok: false, error: e.message }; })
      .finally(() => { updating = false; });
  } catch (e) {
    sendJSON(res, 500, { error: e.message });
  }
}

// ── Settings UI page ─────────────────────────────────────────────────────
function serveUI(_req, res) {
  const html = `<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8"><title>DeepSeek Harness — Обновление</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#101114;color:#f1f3f5;font:14px/1.5 'Segoe UI',system-ui,sans-serif;padding:32px;max-width:640px;margin:0 auto}
h1{font-size:20px;font-weight:600;margin-bottom:4px}
.sub{color:#6b7280;font-size:12px;margin-bottom:24px}
.card{background:#181a1f;border:1px solid #30343d;border-radius:10px;padding:20px;margin-bottom:16px}
.row{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px}
.label{color:#6b7280;font-size:12px;text-transform:uppercase;letter-spacing:.5px}
.value{font-size:14px;font-weight:500}
.badge{display:inline-block;padding:3px 10px;border-radius:12px;font-size:12px;font-weight:600}
.badge-ok{background:#1a3a2a;color:#4ade80}
.badge-new{background:#1a2a3a;color:#60a5fa}
.badge-err{background:#3a1a1a;color:#f87171}
.bar-track{background:#20232a;border-radius:4px;height:6px;margin:12px 0;overflow:hidden}
.bar-fill{background:#4a9eff;border-radius:4px;height:100%;transition:width .3s;width:0}
.btn{border:none;border-radius:8px;padding:10px 24px;font-size:13px;font-weight:600;cursor:pointer}
.btn-primary{background:#4a9eff;color:#101114}
.btn-primary:disabled{opacity:.4;cursor:default}
.log{background:#0d0f14;border:1px solid #252830;border-radius:6px;padding:12px;font:12px/1.6 monospace;color:#4b5058;max-height:220px;overflow-y:auto;white-space:pre-wrap}
.note{color:#fbbf24;font-size:13px;margin-top:12px;display:none}
</style></head><body>
<h1>Обновление DeepSeek Harness</h1>
<p class="sub">Официальный репозиторий deepseek-ai/deepseek-harness · npm registry</p>
<div class="card">
  <div class="row"><span class="label">Установлено</span><span class="value" id="installed">—</span></div>
  <div class="row"><span class="label">Последняя версия</span><span class="value" id="latest">—</span></div>
  <div class="row"><span class="label">Источник</span><span class="value" id="source" style="color:#6b7280">—</span></div>
  <div class="row"><span class="label">Статус</span><span id="status">Проверка…</span></div>
  <div class="bar-track"><div class="bar-fill" id="bar"></div></div>
  <button class="btn btn-primary" id="btn" onclick="doUpdate()" disabled>Проверка…</button>
  <div class="note" id="note">Обновление установлено. Перезапустите приложение, чтобы изменения вступили в силу.</div>
</div>
<div class="log" id="log"></div>
<script>
const $=id=>document.getElementById(id);
let busy=false;
function log(m){const e=$('log'),t=new Date().toLocaleTimeString();e.textContent+='['+t+'] '+m+'\\n';e.scrollTop=e.scrollHeight;}
async function check(){
  try{
    const d=await(await fetch('/api/updater/status')).json();
    if(d.error){$('status').innerHTML='<span class="badge badge-err">'+d.error+'</span>';return;}
    $('installed').textContent=d.installed||'—';
    $('latest').textContent=d.latest||'—';
    $('source').textContent=d.source||'—';
    if(d.lastResult&&!d.lastResult.ok){log('Ошибка прошлого обновления: '+d.lastResult.error);}
    if(d.installed&&d.latest&&d.installed!==d.latest){
      $('status').innerHTML='<span class="badge badge-new">Доступно обновление</span>';
      $('btn').textContent='Обновить до '+d.latest;$('btn').disabled=busy;
    }else if(d.lastResult&&d.lastResult.ok){
      $('status').innerHTML='<span class="badge badge-ok">Обновлено</span>';
      $('note').style.display='block';$('btn').textContent='Обновить';$('btn').disabled=true;
    }else{
      $('status').innerHTML='<span class="badge badge-ok">Актуальная версия</span>';
      $('btn').textContent='Обновить';$('btn').disabled=true;
    }
  }catch(e){$('status').innerHTML='<span class="badge badge-err">'+e.message+'</span>';}
}
async function doUpdate(){
  if(busy)return;busy=true;
  $('btn').disabled=true;$('btn').textContent='Обновление…';$('bar').style.width='0%';
  log('Запуск обновления…');$('bar').style.width='30%';
  try{
    const d=await(await fetch('/api/updater/apply',{method:'POST'})).json();
    log(d.message||d.error||JSON.stringify(d));
    if(d.error){busy=false;check();return;}
    $('bar').style.width='60%';
    let tries=0;
    const poll=setInterval(async()=>{
      tries++;
      try{
        const s=await(await fetch('/api/updater/status')).json();
        if(s.updating===false){
          clearInterval(poll);$('bar').style.width='100%';
          if(s.lastResult&&s.lastResult.ok){log('Готово: версия '+s.lastResult.version);$('note').style.display='block';}
          else if(s.lastResult){log('Ошибка: '+s.lastResult.error);}
          busy=false;check();
        }else{$('bar').style.width=(60+tries)+'%';}
      }catch{}
      if(tries>120)clearInterval(poll);
    },2000);
  }catch(e){log('Ошибка: '+e.message);busy=false;check();}
}
check();
</script></body></html>`;
  res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
  res.end(html);
}

// ── Plugin entry (cordis) ────────────────────────────────────────────────
export default function apply(ctx) {
  // cordis requires services to be declared via inject(); accessing
  // ctx.webServer directly throws "cannot get property without inject".
  ctx.inject(["webServer"], (ctx2) => {
    const ws = ctx2.webServer;
    const disposers = [
      ws.register({ kind: "exact", path: "/updater", handler: serveUI }),
      ws.register({ kind: "exact", path: "/api/updater/status", handler: handleStatus }),
      ws.register({ kind: "exact", path: "/api/updater/check", handler: handleCheck }),
      ws.register({ kind: "exact", path: "/api/updater/apply", handler: handleApply }),
    ];
    log(`updater plugin active (dsh root: ${DSH_ROOT})`);
    return () => {
      for (const d of disposers) d();
      log("updater plugin disposed");
    };
  });
  log("updater plugin loaded (awaiting webServer service)");
}
