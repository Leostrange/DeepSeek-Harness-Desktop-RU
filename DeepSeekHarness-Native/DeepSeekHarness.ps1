[CmdletBinding()]
param(
    [switch]$InstallStartup,
    [switch]$NoBrowser,
    [int]$Port = 3080,
    [int]$StartupTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$AppName = 'DeepSeek Harness'
$DataRoot = Join-Path $env:APPDATA 'DeepSeekHarness'
$DshHome = Join-Path $DataRoot 'data'
$LogDir = Join-Path $DataRoot 'logs'
$LogFile = Join-Path $LogDir 'launcher.log'
$Url = "http://127.0.0.1:$Port"
$MutexName = 'DeepSeekHarnessLauncher'
# Офлайн-бандл harness (если уложен рядом с оболочкой) и его модули для русского патча.
$HarnessBin = Join-Path $PSScriptRoot 'harness\node_modules\@deepseek-ai\dsh\lib\bin.js'
$HarnessModules = Join-Path $PSScriptRoot 'harness\node_modules\@deepseek-ai\dsh\node_modules\@deepseek-ai'
# Портативный Node.js, если установщик положил его рядом с оболочкой.
$BundledNode = Join-Path $PSScriptRoot 'node\node.exe'

New-Item -ItemType Directory -Force -Path $DshHome, $LogDir | Out-Null

function Write-LauncherLog([string]$Message) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $Message"
    Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
}

function Test-Port([int]$Number) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $task = $client.ConnectAsync('127.0.0.1', $Number)
        $ok = $task.Wait(500) -and $client.Connected
        $client.Close()
        return $ok
    } catch { return $false }
}

function Get-NodeExe {
    if (Test-Path $BundledNode) { return $BundledNode }
    return (Get-Command node -ErrorAction Stop).Source
}

function Test-Node {
    try {
        $nodeExe = Get-NodeExe
        $version = (& $nodeExe --version 2>$null).Trim()
        if ($version -notmatch '^v(18|19|20|21|22|23|24)\.') {
            throw "Найден Node.js $version. Нужен Node.js 18 или новее."
        }
    } catch {
        [System.Windows.Forms.MessageBox]::Show("$AppName не может запуститься.`n`nУстановите Node.js LTS с https://nodejs.org/ и запустите оболочку снова.`n`nОшибка: $($_.Exception.Message)", $AppName, 'OK', 'Error') | Out-Null
        exit 1
    }
}

# Открывает интерфейс: предпочитаем нативный клиент, иначе — браузер.
function Open-Ui {
    $exe = @(
        (Join-Path $PSScriptRoot 'NativeClient\DeepSeekHarness.Native.exe'),
        (Join-Path $PSScriptRoot 'NativeClient\publish\DeepSeekHarness.Native.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($exe) {
        Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    } else {
        Start-Process $Url
    }
}

# Русская локализация: применяем при каждом запуске (идемпотентно),
# чтобы патч переживал обновление @deepseek-ai/dsh.
function Apply-RuPatch {
    $patchScript = Join-Path $PSScriptRoot 'i18n-ru\apply-ru.mjs'
    if (-not (Test-Path $patchScript)) {
        Write-LauncherLog 'RU i18n patch skipped (i18n-ru/apply-ru.mjs not found)'
        return
    }
    $nodeExe = Get-NodeExe
    if (Test-Path $HarnessModules) {
        & $nodeExe $patchScript "--base=$HarnessModules" *> $null
    } else {
        & $nodeExe $patchScript *> $null
    }
    Write-LauncherLog 'RU i18n patch applied'
}

try { Add-Type -AssemblyName System.Windows.Forms } catch {}

if ($InstallStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    $shortcutPath = Join-Path $startup 'DeepSeek Harness.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = (Join-Path $PSScriptRoot 'DeepSeekHarness.cmd')
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.Description = 'Запуск DeepSeek Harness'
    $clientExe = @(
        (Join-Path $PSScriptRoot 'NativeClient\DeepSeekHarness.Native.exe'),
        (Join-Path $PSScriptRoot 'NativeClient\publish\DeepSeekHarness.Native.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($clientExe) { $shortcut.IconLocation = "$clientExe,0" }
    $shortcut.Save()
    Write-Host "Автозапуск включён: $shortcutPath"
}

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, $MutexName, [ref]$createdNew)
if (-not $createdNew) {
    if (-not $NoBrowser) { Open-Ui }
    exit 0
}

try {
    Test-Node
    Apply-RuPatch
    $env:DSH_HOME = $DshHome
    $env:NODE_OPTIONS = ''

    if (-not (Test-Port $Port)) {
        Write-LauncherLog "Starting DSH on port $Port with DSH_HOME=$DshHome"
        if (Test-Path $HarnessBin) {
            $nodeExe = Get-NodeExe
            $dshArgs = @($HarnessBin, 'web', '--port', "$Port", '--no-open')
            $process = Start-Process -FilePath $nodeExe -ArgumentList $dshArgs -WorkingDirectory $PSScriptRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $LogDir 'dsh.stdout.log') -RedirectStandardError (Join-Path $LogDir 'dsh.stderr.log')
        } else {
            $dshArgs = @('--yes', '@deepseek-ai/dsh', 'web', '--port', "$Port")
            $process = Start-Process -FilePath 'npx.cmd' -ArgumentList $dshArgs -WorkingDirectory $PSScriptRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $LogDir 'dsh.stdout.log') -RedirectStandardError (Join-Path $LogDir 'dsh.stderr.log')
        }

        # Первый запуск может долго качать пакет через npx, поэтому ждём не 30 секунд,
        # а до StartupTimeoutSeconds с прогрессом в консоли.
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $ready = $false
        $lastReport = 0
        while ($stopwatch.Elapsed.TotalSeconds -lt $StartupTimeoutSeconds) {
            Start-Sleep -Milliseconds 500
            if ($process.HasExited) {
                throw "DeepSeek Harness завершился с кодом $($process.ExitCode). Проверьте $LogDir."
            }
            if (Test-Port $Port) { $ready = $true; break }
            $elapsed = [int]$stopwatch.Elapsed.TotalSeconds
            if ($elapsed -ge $lastReport + 10) {
                $lastReport = $elapsed
                Write-Host "Ожидание DeepSeek Harness... $elapsed с из $StartupTimeoutSeconds с"
            }
        }
        $stopwatch.Stop()
        if (-not $ready) { throw "DeepSeek Harness не открыл порт $Port за $StartupTimeoutSeconds секунд. Проверьте $LogDir." }
        Write-LauncherLog "DSH is ready in $([int]$stopwatch.Elapsed.TotalSeconds)s on port $Port, PID=$($process.Id)"
    } else {
        Write-LauncherLog "DSH is already running on port $Port"
    }

    if (-not $NoBrowser) { Open-Ui }
    Write-Host "$AppName запущен: $Url"
    Write-Host "Постоянные данные: $DshHome"
} catch {
    Write-LauncherLog "ERROR: $($_.Exception.Message)"
    try { [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, $AppName, 'OK', 'Error') | Out-Null } catch {}
    exit 1
} finally {
    if ($mutex) { $mutex.ReleaseMutex(); $mutex.Dispose() }
}
