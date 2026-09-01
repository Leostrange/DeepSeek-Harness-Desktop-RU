from pathlib import Path
import re

p = Path('android/app/src/main/java/io/leostrange/dshandroid/HarnessForegroundService.kt')
s = p.read_text()
s = s.replace('installHarness(runner)\n', 'installHarness(runner, installer.runtimeRoot)\n', 1)

replacement = r'''    private fun installHarness(runner: ProotRunner, runtimeRoot: File) {
        setState(HarnessStage.INSTALLING_HARNESS, "Устанавливаю @deepseek-ai/dsh без lifecycle-скриптов…")
        appendLog("Installing @deepseek-ai/dsh package tree with --ignore-scripts…")
        val env = NativeBuildConfig.npmBuildEnvironment() + mapOf(
            "DSH_NO_LANDLOCK" to "1",
            "CI" to "1",
        )

        val install = runner.start(
            NativeBuildConfig.initialNpmInstallArgs(RuntimePaths.PREFIX),
            env,
        )
        streamProcess(install)
        if (!install.waitFor(12, TimeUnit.MINUTES)) {
            install.destroy()
            if (!install.waitFor(2, TimeUnit.SECONDS)) install.destroyForcibly()
            throw IllegalStateException("npm package download timed out")
        }
        if (install.exitValue() != 0) {
            throw IllegalStateException("npm package download failed (exit ${install.exitValue()})")
        }

        patchNodePtyPostInstall(runtimeRoot)

        setState(HarnessStage.INSTALLING_HARNESS, "Компилирую native-модули Harness…")
        appendLog("Rebuilding DSH lifecycle scripts after Android node-pty patch…")
        val rebuild = runner.start(
            listOf(
                "${RuntimePaths.PREFIX}/bin/sh",
                "-lc",
                NativeBuildConfig.rebuildShellCommand(RuntimePaths.PREFIX),
            ),
            env,
        )
        streamProcess(rebuild)
        if (!rebuild.waitFor(20, TimeUnit.MINUTES)) {
            rebuild.destroy()
            if (!rebuild.waitFor(2, TimeUnit.SECONDS)) rebuild.destroyForcibly()
            throw IllegalStateException("npm rebuild @deepseek-ai/dsh timed out")
        }
        if (rebuild.exitValue() != 0) {
            throw IllegalStateException("npm rebuild @deepseek-ai/dsh failed (exit ${rebuild.exitValue()})")
        }
        appendLog("Harness npm package and native modules installed")
    }

    private fun patchNodePtyPostInstall(runtimeRoot: File) {
        val script = File(
            RuntimePaths.hostPrefix(runtimeRoot),
            "lib/node_modules/@deepseek-ai/dsh/node_modules/node-pty/scripts/post-install.js",
        )
        require(script.isFile) { "node-pty post-install.js not found" }
        var text = script.readText()
        if ("DSH Android: skip release cleanup under PRoot" in text) return

        val anchor = "console.log('\\x1b[32m> Cleaning release folder...\\x1b[0m');"
        require(anchor in text) { "Unsupported node-pty post-install.js layout" }
        val androidGuard = """

// DSH Android: PRoot may return EPERM for lstat() on the freshly linked
// native addon inside obj.target. Release/pty.node is complete already.
if (os.platform() === 'android') {
  console.log('> DSH Android: skip release cleanup under PRoot');
  process.exit(0);
}
""".trimIndent()
        text = text.replace(anchor, anchor + "\n" + androidGuard)
        script.writeText(text)
        appendLog("node-pty postinstall patched: Android cleanup disabled")
    }

'''

pattern = re.compile(
    r'    private fun installHarness\(runner: ProotRunner\) \{.*?\n    private fun installSharpWasmFallback\(runner: ProotRunner, runtimeRoot: File\) \{',
    re.S,
)
s2, n = pattern.subn(
    lambda _: replacement + '    private fun installSharpWasmFallback(runner: ProotRunner, runtimeRoot: File) {',
    s,
    count=1,
)
if n != 1:
    raise SystemExit('installHarness patch failed')

p.write_text(s2)
