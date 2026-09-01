package io.leostrange.dshandroid.runtime

import android.content.Context
import android.os.Build
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

class RuntimeInstaller(private val context: Context) {
    val runtimeRoot: File = File(context.filesDir, "runtime-root")
    private val packageCache = File(context.cacheDir, "termux-packages")
    private val marker = File(runtimeRoot, ".dsh-runtime-installed")
    private val extractor = DebExtractor(File(context.cacheDir, "deb-extract"))

    fun isInstalled(): Boolean {
        if (!marker.isFile) return false
        val prefix = RuntimePaths.hostPrefix(runtimeRoot)
        return pathPresent(File(prefix, "bin/proot")) &&
            pathPresent(File(prefix, "bin/node")) &&
            pathPresent(File(prefix, "bin/npm"))
    }

    fun ensureInstalled(force: Boolean = false, progress: (RuntimeInstallProgress) -> Unit = {}) {
        requireSupportedAbi()
        if (!force && isInstalled()) {
            progress(RuntimeInstallProgress("Готово", 1, 1, "Среда Node.js уже установлена"))
            return
        }

        progress(RuntimeInstallProgress("Подготовка", 0, 1, "Подготавливаю встроенную среду…"))
        runtimeRoot.deleteRecursively()
        packageCache.mkdirs()
        RuntimePaths.hostPrefix(runtimeRoot).mkdirs()
        RuntimePaths.hostHome(runtimeRoot).mkdirs()
        RuntimePaths.hostTmp(runtimeRoot).mkdirs()
        File(runtimeRoot, "tmp").mkdirs()

        progress(RuntimeInstallProgress("Индекс", 0, 1, "Скачиваю индекс пакетов Termux…"))
        val (repoBase, indexText) = downloadPackageIndex()
        val index = TermuxPackageIndex.parse(indexText)
        val selected = index.resolve(ROOT_PACKAGES)
        require(selected.isNotEmpty()) { "Termux package resolver returned an empty set" }

        selected.forEachIndexed { i, pkg ->
            progress(
                RuntimeInstallProgress(
                    "Пакеты",
                    i,
                    selected.size,
                    "${i + 1}/${selected.size}: ${pkg.name} ${pkg.version}",
                )
            )
            val deb = File(packageCache, "${sanitize(pkg.name)}-${sanitize(pkg.version)}.deb")
            if (!deb.isFile || !verifySha256(deb, pkg.sha256)) {
                deb.delete()
                download(repoBase + pkg.filename.removePrefix("/"), deb)
            }
            if (!verifySha256(deb, pkg.sha256)) {
                deb.delete()
                throw IllegalStateException("SHA-256 mismatch for ${pkg.name}")
            }
            extractor.extract(deb, runtimeRoot)
        }

        val prefix = RuntimePaths.hostPrefix(runtimeRoot)
        listOf("proot", "node", "npm", "npx", "bash", "sh", "env", "dsh").forEach { name ->
            File(prefix, "bin/$name").takeIf { it.exists() || runCatching { android.system.Os.lstat(it.path) }.isSuccess }
                ?.setExecutable(true, true)
        }
        File(prefix, "bin/proot").setExecutable(true, true)
        File(prefix, "bin/node").setExecutable(true, true)
        createCompatibilityLinks()

        marker.parentFile?.mkdirs()
        marker.writeText(
            buildString {
                appendLine("repo=$repoBase")
                selected.forEach { appendLine("${it.name}=${it.version}") }
            }
        )
        progress(RuntimeInstallProgress("Готово", selected.size, selected.size, "Среда Node.js установлена"))
    }

    fun clear() {
        runtimeRoot.deleteRecursively()
        marker.delete()
    }

    private fun requireSupportedAbi() {
        val supported = Build.SUPPORTED_ABIS.any { it == "arm64-v8a" }
        require(supported) {
            "Пока поддерживается только ARM64 (arm64-v8a). Устройство: ${Build.SUPPORTED_ABIS.joinToString()}"
        }
    }

    private fun download(url: String, destination: File) {
        destination.parentFile?.mkdirs()
        val temp = File(destination.parentFile, destination.name + ".part")
        temp.delete()
        val connection = (URL(url).openConnection() as HttpURLConnection).apply {
            connectTimeout = 20_000
            readTimeout = 120_000
            instanceFollowRedirects = true
            requestMethod = "GET"
            setRequestProperty("User-Agent", "DeepSeekHarness-Android/1.1")
        }
        try {
            connection.connect()
            if (connection.responseCode !in 200..299) {
                throw IllegalStateException("HTTP ${connection.responseCode} while downloading $url")
            }
            connection.inputStream.use { input ->
                FileOutputStream(temp).use { output -> input.copyTo(output, 128 * 1024) }
            }
            if (!temp.renameTo(destination)) {
                temp.copyTo(destination, overwrite = true)
                temp.delete()
            }
        } finally {
            connection.disconnect()
            temp.delete()
        }
    }

    private fun downloadPackageIndex(): Pair<String, String> {
        val errors = mutableListOf<String>()
        for (repoBase in REPO_BASES) {
            val url = "${repoBase}dists/stable/main/binary-aarch64/Packages"
            val indexFile = File(packageCache, "Packages")
            try {
                download(url, indexFile)
                val text = indexFile.bufferedReader().use { it.readText() }
                if (text.contains("Package:")) return repoBase to text
                errors += "$url: downloaded index is invalid"
            } catch (t: Throwable) {
                errors += "$url: ${t.message ?: t.javaClass.simpleName}"
            } finally {
                indexFile.delete()
            }
        }
        throw IllegalStateException("Не удалось скачать индекс Termux. " + errors.joinToString(" | "))
    }

    private fun verifySha256(file: File, expected: String?): Boolean {
        if (!file.isFile) return false
        if (expected.isNullOrBlank()) return true
        val digest = MessageDigest.getInstance("SHA-256")
        FileInputStream(file).use { input ->
            val buffer = ByteArray(128 * 1024)
            while (true) {
                val count = input.read(buffer)
                if (count < 0) break
                digest.update(buffer, 0, count)
            }
        }
        val actual = digest.digest().joinToString("") { "%02x".format(it.toInt() and 0xff) }
        return actual.equals(expected, ignoreCase = true)
    }

    private fun createCompatibilityLinks() {
        val links = mapOf(
            "usr/bin/env" to "${RuntimePaths.PREFIX}/bin/env",
            "usr/bin/node" to "${RuntimePaths.PREFIX}/bin/node",
            "bin/sh" to "${RuntimePaths.PREFIX}/bin/sh",
            "bin/bash" to "${RuntimePaths.PREFIX}/bin/bash",
        )
        links.forEach { (guestPath, target) ->
            val hostPath = File(runtimeRoot, guestPath)
            hostPath.parentFile?.mkdirs()
            runCatching { android.system.Os.lstat(hostPath.path) }.onSuccess {
                hostPath.delete()
            }
            android.system.Os.symlink(target, hostPath.path)
        }
    }

    private fun pathPresent(file: File): Boolean =
        file.isFile || runCatching { android.system.Os.lstat(file.path) }.isSuccess

    private fun sanitize(value: String): String = value.replace(Regex("[^A-Za-z0-9._+-]"), "_")

    companion object {
        private val REPO_BASES = listOf(
            "https://packages-cf.termux.dev/apt/termux-main/",
            "https://packages.termux.dev/apt/termux-main/",
            "https://ftp.fau.de/termux/termux-main/",
        )
        private val ROOT_PACKAGES = setOf(
            "proot",
            "nodejs-lts",
            "npm",
            "bash",
            "coreutils",
            "ca-certificates",
            "openssl",
            "procps",
            "termux-exec",
        )
    }
}

data class RuntimeInstallProgress(
    val phase: String,
    val current: Int,
    val total: Int,
    val message: String,
)
