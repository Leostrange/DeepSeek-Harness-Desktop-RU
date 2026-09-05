# Updates the bundled DeepSeek Harness to the latest published version.
#   .\Update.ps1            - check + confirm + update
#   .\Update.ps1 -Force     - update without confirmation (used by the client)
[CmdletBinding()]
param(
    [switch]$Force,
    [int]$Port = 3080
)
$ErrorActionPreference = 'Stop'

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$HarnessDir = Join-Path $ScriptRoot 'harness\node_modules\@deepseek-ai\dsh'
$PatchScript = Join-Path $ScriptRoot 'i18n-ru\apply-ru.mjs'
$BundledNode = Join-Path $ScriptRoot 'node\node.exe'
$LogDir = Join-Path $env:APPDATA 'DeepSeekHarness\logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$LogFile = Join-Path $LogDir 'update.log'
function Log([string]$m) { Add-Content -LiteralPath $LogFile -Value "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $m" -Encoding UTF8 }
Log '=== Update.ps1 started ==='

try { Add-Type -AssemblyName System.Windows.Forms } catch {}

if (-not (Test-Path $HarnessDir)) {
    Log 'bundle not found'
    Write-Error 'Offline bundle not found. Update requires the bundled harness.'
    exit 1
}

$installed = (Get-Content (Join-Path $HarnessDir 'package.json') -Raw | ConvertFrom-Json).version
Log "installed=$installed"

try {
    $latest = (Invoke-RestMethod -Uri 'https://registry.npmjs.org/@deepseek-ai/dsh/latest' -TimeoutSec 30).version
} catch {
    Log "registry check failed: $($_.Exception.Message)"
    Write-Error 'Cannot reach the npm registry. Check your connection.'
    exit 1
}
Log "latest=$latest"

if (-not $Force -and $installed -eq $latest) {
    Write-Host "Already up to date ($latest)."
    exit 0
}
if (-not $Force) {
    $choice = [System.Windows.Forms.MessageBox]::Show("New version $latest (installed $installed). Update now?", 'DeepSeek Harness', 'YesNo', 'Question')
    if ($choice -ne 'Yes') { Log 'update cancelled'; Write-Host 'Update cancelled.'; exit 0 }
}

# Free the harness port so the bundle files can be replaced.
Log "freeing port $Port"
try {
    $owner = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1 -ExpandProperty OwningProcess
    if ($owner) { Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue; Log "stopped harness pid=$owner" }
} catch { }

$tgz = Join-Path $env:TEMP "dsh-$latest.tgz"
$stage = Join-Path $env:TEMP "dsh-update-$latest"
try {
    Write-Host "Downloading dsh $latest ..."
    Log "downloading dsh $latest from npm registry"
    Invoke-WebRequest -Uri "https://registry.npmjs.org/@deepseek-ai/dsh/-/dsh-$latest.tgz" -OutFile $tgz -TimeoutSec 600
    Log "downloaded $tgz"

    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { throw 'tar.exe not found' }
    Log 'extracting tarball'
    & $tar -xzf $tgz -C $stage 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'tarball extraction failed' }
    Log 'tarball extracted'

    Write-Host 'Installing dependencies (npm install) ...'
    Log 'npm install starting'
    $npm = if (Test-Path (Join-Path $ScriptRoot 'node\npm.cmd')) { Join-Path $ScriptRoot 'node\npm.cmd' } else { 'npm.cmd' }
    Log "npm path: $npm (exists=$(Test-Path $npm))"
    Log "node path: $(Join-Path $ScriptRoot 'node\node.exe') (exists=$(Test-Path (Join-Path $ScriptRoot 'node\node.exe')))"
    # Ensure node is on PATH for the job
    $env:PATH = "$(Join-Path $ScriptRoot 'node');$env:PATH"
    Push-Location (Join-Path $stage 'package')
    $npmJob = Start-Job -ScriptBlock {
        param($workDir, $npmExe)
        Set-Location $workDir
        & $npmExe install --omit=dev --no-audit --no-fund --no-progress --prefer-offline --maxsockets=16 2>&1
        return $LASTEXITCODE
    } -ArgumentList (Get-Location).Path, $npm
    $completed = Wait-Job $npmJob -Timeout 600
    if (-not $completed) {
        Stop-Job $npmJob; Remove-Job $npmJob -Force
        Pop-Location
        throw 'npm install timed out after 10 minutes. Check your network connection.'
    }
    $npmExit = Receive-Job $npmJob
    Remove-Job $npmJob
    if ($npmExit -ne 0) { Pop-Location; throw "npm install failed with exit code $npmExit" }
    Pop-Location
    Log 'npm install done'

    Log 'cleaning up unnecessary files'
    Get-ChildItem (Join-Path $stage 'package') -Recurse -File -Include *.map,*.d.ts,*.md,LICENSE -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    Log 'applying RU patch'
    $nodeExe = if (Test-Path $BundledNode) { $BundledNode } else { 'node' }
    $modulesRoot = Join-Path $stage 'package\node_modules\@deepseek-ai'
    if (Test-Path $modulesRoot) {
        & $nodeExe $PatchScript "--base=$modulesRoot" 2>&1 | Out-Null
        Log 'RU patch applied'
    } else {
        Log 'RU patch skipped (no @deepseek-ai modules dir)'
    }

    Log 'replacing harness bundle'
    Remove-Item $HarnessDir -Recurse -Force
    Move-Item (Join-Path $stage 'package') $HarnessDir
    Log "updated to $latest"
    Write-Host "Updated to $latest."
    exit 0
} finally {
    Log 'cleanup: removing temp files'
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $tgz -Force -ErrorAction SilentlyContinue
    Log '=== Update.ps1 finished ==='
}
