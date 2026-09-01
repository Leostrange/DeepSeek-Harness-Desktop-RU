package io.leostrange.dshandroid

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import io.leostrange.dshandroid.runtime.NativeBuildConfig
import io.leostrange.dshandroid.runtime.ProotRunner
import io.leostrange.dshandroid.runtime.RuntimeInstallProgress
import io.leostrange.dshandroid.runtime.RuntimeInstaller
import io.leostrange.dshandroid.runtime.RuntimePaths
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.ArrayDeque
import java.util.concurrent.TimeUnit

class HarnessForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var launchJob: Job? = null
    @Volatile private var harnessProcess: Process? = null
    private val logLines = ArrayDeque<String>()
    private lateinit var logFile: File

    override fun onCreate() {
        super.onCreate()
        logFile = File(filesDir, "logs/harness.log").apply { parentFile?.mkdirs() }
        startForeground(NOTIF_ID, buildNotification("Подготовка…"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action ?: ACTION_START) {
            ACTION_STOP -> stopHarnessAndSelf()
            ACTION_REINSTALL -> startHarness(forceReinstall = true)
            else -> startHarness(forceReinstall = false)
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    private fun startHarness(forceReinstall: Boolean) {
        if (launchJob?.isActive == true) return
        launchJob = scope.launch {
            try {
                if (!forceReinstall && isReachable(HARNESS_URL)) {
                    setState(HarnessStage.RUNNING, "Harness уже работает на $HARNESS_URL")
                    return@launch
                }

                stopOwnedProcess()
                clearLog()
                val installer = RuntimeInstaller(this@HarnessForegroundService)
                if (forceReinstall) {
                    setState(HarnessStage.BOOTSTRAPPING, "Переустанавливаю встроенную среду…")
                    installer.clear()
                }

                setState(HarnessStage.BOOTSTRAPPING, "Подготавливаю Node.js и Termux runtime…")
                installer.ensureInstalled(force = forceReinstall) { updateInstallProgress(it) }

                val runner = ProotRunner(installer.runtimeRoot)
                setState(HarnessStage.VERIFYING, "Проверяю Node.js…")
                val node = runner.runCapture(listOf("${RuntimePaths.PREFIX}/bin/node", "--version"), 30)
                    .requireSuccess("node --version")
                appendLog("Node: ${node.output}")

                val npm = runner.runCapture(listOf("${RuntimePaths.PREFIX}/bin/npm", "--version"), 30)
                    .requireSuccess("npm --version")
                appendLog("npm: ${npm.output}")

                if (!harnessInstalled(installer.runtimeRoot)) {
                    prepareNativeBuild(runner, installer.runtimeRoot)
                    installHarness(runner)
                    installSharpWasmFallback(runner, installer.runtimeRoot)
                    installDshLauncher(installer.runtimeRoot)
                }

                verifyHarnessNativeModules(runner, installer.runtimeRoot)
                launchHarness(runner)
            } catch (cancelled: kotlinx.coroutines.CancellationException) {
                throw cancelled
            } catch (t: Throwable) {
                appendLog("ERROR: ${t.stackTraceToString()}")
                setState(
                    HarnessStage.ERROR,
                    "Не удалось запустить DeepSeek Harness",
                    error = t.message ?: t.javaClass.simpleName,
                )
                stopOwnedProcess()
            }
        }
    }

    private fun updateInstallProgress(progress: RuntimeInstallProgress) {
        HarnessRuntimeState.update {
            it.copy(
                stage = HarnessStage.BOOTSTRAPPING,
                message = progress.message,
                progressCurrent = progress.current,
                progressTotal = progress.total,
                error = null,
            )
        }
        updateNotification(progress.message)
    }

    private fun prepareNativeBuild(runner: ProotRunner, runtimeRoot: File) {
        setState(HarnessStage.INSTALLING_HARNESS, "Подготавливаю Android toolchain для native-модулей…")
        appendLog("Preparing Koffi/node-pty native build toolchain…")

        val allowScripts = runner.runCapture(
            listOf(
                "${RuntimePaths.PREFIX}/bin/npm", "config", "set",
                "allow-scripts=${NativeBuildConfig.ALLOW_SCRIPTS}", "--location=user",
            ),
            30,
        )
        if (allowScripts.exitCode != 0) {
            appendLog("npm allow-scripts warning: ${allowScripts.output}")
        }

        val nodeGyp = "${RuntimePaths.PREFIX}/lib/node_modules/npm/node_modules/node-gyp/bin/node-gyp.js"
        val headers = runner.runCapture(
            listOf("${RuntimePaths.PREFIX}/bin/node", nodeGyp, "install"),
            240,
        )
        if (headers.exitCode != 0) {
            appendLog("node-gyp headers warning: ${headers.output}")
        } else if (headers.output.isNotBlank()) {
            appendLog("node-gyp headers: ${headers.output}")
        }
        patchCommonGypi(runtimeRoot)
    }

    private fun patchCommonGypi(runtimeRoot: File) {
        val roots = listOf(
            File(RuntimePaths.hostHome(runtimeRoot), ".cache/node-gyp"),
            File(RuntimePaths.hostPrefix(runtimeRoot), "include/node"),
        )
        var found = 0
        var patched = 0
        roots.filter { it.exists() }.forEach { root ->
            root.walkTopDown()
                .filter { it.isFile && it.name == "common.gypi" }
                .forEach fileLoop@{ file ->
                    found++
                    val text = file.readText()
                    if ("android_ndk_path%'" in text) return@fileLoop
                    val marker = "'variables': {"
                    val index = text.indexOf(marker)
                    if (index >= 0) {
                        val insertAt = index + marker.length
                        file.writeText(
                            text.substring(0, insertAt) +
                                "\n    'android_ndk_path%': ''," +
                                text.substring(insertAt)
                        )
                        patched++
                    }
                }
        }
        appendLog("common.gypi: found=$found patched=$patched")
    }

    private fun installHarness(runner: ProotRunner) {
        setState(HarnessStage.INSTALLING_HARNESS, "Компилирую и устанавливаю @deepseek-ai/dsh…")
        appendLog("Installing @deepseek-ai/dsh from npm with Android native build flags…")
        val env = NativeBuildConfig.npmBuildEnvironment() + mapOf(
            "DSH_NO_LANDLOCK" to "1",
            "CI" to "1",
        )
        val process = runner.start(
            listOf(
                "${RuntimePaths.PREFIX}/bin/npm",
                "install",
                "--global",
                "--foreground-scripts",
                "--no-audit",
                "--no-fund",
                "@deepseek-ai/dsh",
            ),
            env,
        )
        streamProcess(process)
        if (!process.waitFor(20, TimeUnit.MINUTES)) {
            process.destroy()
            if (!process.waitFor(2, TimeUnit.SECONDS)) process.destroyForcibly()
            throw IllegalStateException("npm install @deepseek-ai/dsh timed out")
        }
        if (process.exitValue() != 0) {
            throw IllegalStateException("npm install @deepseek-ai/dsh failed (exit ${process.exitValue()})")
        }
        appendLog("Harness npm package installed")
    }

    private fun installSharpWasmFallback(runner: ProotRunner, runtimeRoot: File) {
        val prefix = RuntimePaths.hostPrefix(runtimeRoot)
        val dsh = File(prefix, "lib/node_modules/@deepseek-ai/dsh")
        val sharpPackage = File(dsh, "node_modules/sharp/package.json")
        if (!sharpPackage.isFile) {
            appendLog("sharp package not present; WASM fallback not needed")
            return
        }

        val version = JSONObject(sharpPackage.readText()).getString("version")
        val target = File(dsh, "node_modules/@img/sharp-wasm32")
        if (target.resolve("lib").listFiles()?.any { it.extension == "wasm" } == true) {
            appendLog("sharp WASM fallback already installed")
            return
        }

        setState(HarnessStage.INSTALLING_HARNESS, "Устанавливаю sharp WebAssembly fallback…")
        appendLog("Installing @img/sharp-wasm32@$version…")
        val workGuest = "${RuntimePaths.HOME}/.dsh-sharp-wasm"
        val result = runner.runCapture(
            listOf(
                "${RuntimePaths.PREFIX}/bin/npm", "install",
                "--prefix", workGuest,
                "--no-save", "--no-audit", "--no-fund",
                "@img/sharp-wasm32@$version",
            ),
            420,
        ).requireSuccess("sharp wasm fallback")
        if (result.output.isNotBlank()) appendLog(result.output)

        val workHost = File(RuntimePaths.hostHome(runtimeRoot), ".dsh-sharp-wasm/node_modules")
        val source = File(workHost, "@img/sharp-wasm32")
        require(source.isDirectory) { "sharp-wasm32 package was not installed" }
        target.deleteRecursively()
        target.parentFile?.mkdirs()
        source.copyRecursively(target, overwrite = true)

        val emnapiSource = File(workHost, "@emnapi")
        if (emnapiSource.isDirectory) {
            val emnapiTarget = File(dsh, "node_modules/@emnapi")
            emnapiTarget.deleteRecursively()
            emnapiSource.copyRecursively(emnapiTarget, overwrite = true)
        }
        File(RuntimePaths.hostHome(runtimeRoot), ".dsh-sharp-wasm").deleteRecursively()
        appendLog("sharp WASM fallback installed")
    }

    private fun installDshLauncher(runtimeRoot: File) {
        val prefix = RuntimePaths.hostPrefix(runtimeRoot)
        val dshDir = "${RuntimePaths.PREFIX}/lib/node_modules/@deepseek-ai/dsh"
        val launcher = File(prefix, "bin/dsh")
        launcher.delete()
        launcher.writeText(
            "#!${RuntimePaths.PREFIX}/bin/sh\n" +
                "exec ${RuntimePaths.PREFIX}/bin/node --expose-internals $dshDir/lib/bin.js \"\$@\"\n"
        )
        launcher.setExecutable(true, true)
        appendLog("dsh launcher installed with --expose-internals")
    }

    private fun verifyHarnessNativeModules(runner: ProotRunner, runtimeRoot: File) {
        setState(HarnessStage.VERIFYING, "Проверяю native-модули Harness…")
        val dshGuest = "${RuntimePaths.PREFIX}/lib/node_modules/@deepseek-ai/dsh"
        runner.runCapture(
            listOf(
                "${RuntimePaths.PREFIX}/bin/node", "-e",
                "require('$dshGuest/node_modules/koffi')",
            ),
            30,
        ).requireSuccess("koffi verification")
        appendLog("koffi: OK")

        val nodePty = File(
            RuntimePaths.hostPrefix(runtimeRoot),
            "lib/node_modules/@deepseek-ai/dsh/node_modules/node-pty/build/Release/pty.node",
        )
        require(nodePty.isFile) { "node-pty native module was not built" }
        appendLog("node-pty: OK")

        val dshVersion = runner.runCapture(
            listOf("${RuntimePaths.PREFIX}/bin/dsh", "--version"),
            30,
        ).requireSuccess("dsh --version")
        appendLog("dsh: ${dshVersion.output}")
    }

    private fun launchHarness(runner: ProotRunner) {
        setState(HarnessStage.STARTING, "Запускаю Harness на 127.0.0.1:3080…")
        appendLog("Starting dsh web --host 127.0.0.1 --port 3080 --no-open")
        val process = runner.start(
            listOf(
                "${RuntimePaths.PREFIX}/bin/dsh",
                "web",
                "--host", "127.0.0.1",
                "--port", "3080",
                "--no-open",
            ),
            mapOf(
                "DSH_NO_LANDLOCK" to "1",
                "DSH_HOME" to "${RuntimePaths.HOME}/.dsh",
            ),
        )
        harnessProcess = process
        streamProcess(process)

        val deadline = System.currentTimeMillis() + 90_000
        while (System.currentTimeMillis() < deadline) {
            if (!process.isAlive) {
                throw IllegalStateException("Harness process exited with code ${process.exitValue()}")
            }
            if (isReachable(HARNESS_URL)) {
                setState(HarnessStage.RUNNING, "DeepSeek Harness запущен")
                process.waitFor()
                if (harnessProcess === process) harnessProcess = null
                if (HarnessRuntimeState.state.value.stage != HarnessStage.STOPPED) {
                    throw IllegalStateException("Harness stopped with code ${process.exitValue()}")
                }
                return
            }
            Thread.sleep(500)
        }
        throw IllegalStateException("Harness не открыл порт 3080 за 90 секунд")
    }

    private fun streamProcess(process: Process) {
        Thread {
            try {
                process.inputStream.bufferedReader().useLines { lines ->
                    lines.forEach(::appendLog)
                }
            } catch (_: Throwable) {
            }
        }.apply {
            name = "dsh-runtime-log"
            isDaemon = true
            start()
        }
    }

    @Synchronized
    private fun appendLog(line: String) {
        val clean = line.trimEnd()
        if (clean.isBlank()) return
        logFile.appendText(clean + "\n")
        logLines.addLast(clean)
        while (logLines.size > MAX_LOG_LINES) logLines.removeFirst()
        val tail = logLines.joinToString("\n")
        HarnessRuntimeState.update { it.copy(logTail = tail) }
    }

    @Synchronized
    private fun clearLog() {
        logLines.clear()
        logFile.parentFile?.mkdirs()
        logFile.writeText("")
        HarnessRuntimeState.update { it.copy(logTail = "") }
    }

    private fun harnessInstalled(runtimeRoot: File): Boolean {
        return File(RuntimePaths.hostPrefix(runtimeRoot), "lib/node_modules/@deepseek-ai/dsh/package.json").isFile &&
            File(RuntimePaths.hostPrefix(runtimeRoot), "bin/dsh").isFile
    }

    private fun setState(stage: HarnessStage, message: String, error: String? = null) {
        HarnessRuntimeState.update {
            it.copy(
                stage = stage,
                message = message,
                progressCurrent = 0,
                progressTotal = 0,
                error = error,
            )
        }
        updateNotification(message)
    }

    private fun isReachable(url: String): Boolean {
        return try {
            val connection = (URL(url).openConnection() as HttpURLConnection).apply {
                connectTimeout = 1_000
                readTimeout = 1_000
                requestMethod = "GET"
            }
            try {
                connection.connect()
                connection.responseCode in 100..599
            } finally {
                connection.disconnect()
            }
        } catch (_: Throwable) {
            false
        }
    }

    private fun stopHarnessAndSelf() {
        HarnessRuntimeState.update { it.copy(stage = HarnessStage.STOPPED, message = "Harness остановлен", error = null) }
        launchJob?.cancel()
        launchJob = null
        stopOwnedProcess()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    @Synchronized
    private fun stopOwnedProcess() {
        val process = harnessProcess ?: return
        harnessProcess = null
        runCatching { process.destroy() }
        runCatching {
            if (!process.waitFor(2, TimeUnit.SECONDS)) process.destroyForcibly()
        }
    }

    private fun updateNotification(text: String) {
        val nm = getSystemService(NOTIFICATION_SERVICE) as android.app.NotificationManager
        nm.notify(NOTIF_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val openIntent = Intent(this, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
        }
        val pending = PendingIntent.getActivity(
            this,
            0,
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val stopIntent = Intent(this, HarnessForegroundService::class.java).apply { action = ACTION_STOP }
        val stopPending = PendingIntent.getService(
            this,
            1,
            stopIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, DshApplication.CHANNEL_ID)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle(getString(R.string.notif_title))
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setOngoing(true)
            .setContentIntent(pending)
            .addAction(android.R.drawable.ic_delete, getString(R.string.btn_stop), stopPending)
            .build()
    }

    override fun onDestroy() {
        launchJob?.cancel()
        stopOwnedProcess()
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "io.leostrange.dshandroid.START"
        const val ACTION_STOP = "io.leostrange.dshandroid.STOP"
        const val ACTION_REINSTALL = "io.leostrange.dshandroid.REINSTALL"
        const val NOTIF_ID = 1001
        private const val HARNESS_URL = "http://127.0.0.1:3080"
        private const val MAX_LOG_LINES = 80
    }
}
