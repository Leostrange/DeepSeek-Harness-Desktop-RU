package io.leostrange.dshandroid.runtime

object NativeBuildConfig {
    val requiredPackages: Set<String> = setOf(
        "cmake",
        "clang",
        "make",
        "python",
        "binutils",
        "pkg-config",
        "libandroid-spawn",
        "termux-tools",
    )

    const val allowScripts = "@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs,pnpm"
    private const val androidTarget = "aarch64-linux-android30"

    fun npmBuildEnvironment(prefix: String): Map<String, String> = mapOf(
        "CFLAGS" to "-target $androidTarget",
        "CXXFLAGS" to "-target $androidTarget",
        "CMAKE_BUILD_PARALLEL_LEVEL" to "2",
        "npm_config_nodedir" to prefix,
    )
}
