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

    const val ALLOW_SCRIPTS = "@deepseek-ai/dsh-subprocess-local,koffi,node-pty,@google/genai,protobufjs,pnpm"
    private const val ANDROID_TARGET = "aarch64-linux-android30"

    fun npmBuildEnvironment(): Map<String, String> = mapOf(
        "CFLAGS" to "-target $ANDROID_TARGET",
        "CXXFLAGS" to "-target $ANDROID_TARGET",
        "CMAKE_BUILD_PARALLEL_LEVEL" to "2",
    )
}
