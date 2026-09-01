package io.leostrange.dshandroid.runtime

import android.system.Os
import org.apache.commons.compress.archivers.ar.ArArchiveInputStream
import org.apache.commons.compress.archivers.tar.TarArchiveEntry
import org.apache.commons.compress.archivers.tar.TarArchiveInputStream
import org.apache.commons.compress.compressors.gzip.GzipCompressorInputStream
import org.apache.commons.compress.compressors.xz.XZCompressorInputStream
import java.io.BufferedInputStream
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream
import java.io.InputStream

class DebExtractor(private val scratchDir: File) {
    fun extract(debFile: File, runtimeRoot: File) {
        scratchDir.mkdirs()
        val dataArchive = File.createTempFile("termux-data-", ".tar", scratchDir)
        var compression: Compression? = null
        try {
            ArArchiveInputStream(BufferedInputStream(FileInputStream(debFile))).use { ar ->
                while (true) {
                    val entry = ar.nextEntry ?: break
                    when (entry.name.removePrefix("./")) {
                        "data.tar.xz" -> {
                            FileOutputStream(dataArchive).use { ar.copyTo(it) }
                            compression = Compression.XZ
                            break
                        }
                        "data.tar.gz" -> {
                            FileOutputStream(dataArchive).use { ar.copyTo(it) }
                            compression = Compression.GZIP
                            break
                        }
                        "data.tar" -> {
                            FileOutputStream(dataArchive).use { ar.copyTo(it) }
                            compression = Compression.NONE
                            break
                        }
                        "data.tar.zst", "data.tar.zstd" -> {
                            throw IllegalStateException("Zstandard .deb payload is not supported by this build")
                        }
                    }
                }
            }
            val kind = compression ?: throw IllegalStateException("No supported data.tar payload in ${debFile.name}")
            FileInputStream(dataArchive).use { raw ->
                val decompressed: InputStream = when (kind) {
                    Compression.XZ -> XZCompressorInputStream(BufferedInputStream(raw))
                    Compression.GZIP -> GzipCompressorInputStream(BufferedInputStream(raw))
                    Compression.NONE -> BufferedInputStream(raw)
                }
                decompressed.use { extractTar(it, runtimeRoot) }
            }
        } finally {
            dataArchive.delete()
        }
    }

    private fun extractTar(input: InputStream, runtimeRoot: File) {
        val hardLinks = mutableListOf<Pair<File, String>>()
        TarArchiveInputStream(BufferedInputStream(input)).use { tar ->
            while (true) {
                val entry = tar.nextTarEntry ?: break
                val normalizedEntryName = entry.name.replace('\\', '/').removePrefix("./")
                if (normalizedEntryName.isBlank() || normalizedEntryName == ".") continue
                val output = RuntimePaths.safeResolve(runtimeRoot, entry.name)
                when {
                    entry.isDirectory -> {
                        output.mkdirs()
                        chmod(output, entry.mode)
                    }
                    entry.isSymbolicLink -> {
                        output.parentFile?.mkdirs()
                        deleteWithoutFollowing(output)
                        Os.symlink(entry.linkName, output.path)
                    }
                    entry.isLink -> {
                        output.parentFile?.mkdirs()
                        hardLinks += output to entry.linkName
                    }
                    entry.isFile -> {
                        output.parentFile?.mkdirs()
                        deleteWithoutFollowing(output)
                        FileOutputStream(output).use { tar.copyTo(it) }
                        chmod(output, entry.mode)
                    }
                    else -> Unit
                }
            }
        }
        hardLinks.forEach { (output, targetName) ->
            val target = RuntimePaths.safeResolve(runtimeRoot, targetName)
            deleteWithoutFollowing(output)
            Os.link(target.path, output.path)
        }
    }

    private fun chmod(file: File, mode: Int) {
        runCatching { Os.chmod(file.path, mode and 0x1FF) }
    }

    private fun deleteWithoutFollowing(file: File) {
        runCatching { Os.lstat(file.path) }.onSuccess { file.delete() }
    }

    private enum class Compression { XZ, GZIP, NONE }
}
