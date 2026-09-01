# Android Web UI + Fast Bootstrap Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Open the normal DeepSeek Harness Web UI on Android and avoid repeating expensive Termux/npm/native bootstrap work when the installed layers are already valid.

**Architecture:** Introduce explicit versioned bootstrap layers (runtime, DSH package, native modules, UI onboarding) and validate each independently. Keep the official Harness UI; Android-specific code only prepares/patches the environment and seeds the Harness notice acknowledgement using the installed DSH version's own state mechanism.

**Tech Stack:** Kotlin, Android foreground service, PRoot/Termux, Node.js/npm/node-gyp, WebView, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-09-01-android-webui-fast-bootstrap-design.md`

## Global Constraints

- ARM64 only.
- Keep targetSdk 28.
- Keep development on `temp`; do not merge to `main` without explicit approval.
- Preserve the official DeepSeek Harness Web UI.
- Full runtime reset must be separate from normal Harness reinstall.

---

### Task 1: Bootstrap layer model and marker validation

**Files:**
- Create: `android/app/src/main/java/io/leostrange/dshandroid/runtime/BootstrapLayers.kt`
- Test: `android/app/src/test/java/io/leostrange/dshandroid/runtime/BootstrapLayersTest.kt`

**Interfaces:**
- Produces: `BootstrapFingerprint`, `BootstrapLayerState`, `BootstrapLayers.evaluate(...)`.
- Consumes: Node version, DSH version, Android native target, patch schema integers, marker file contents.

- [ ] **Step 1: Write failing tests** covering: all markers valid => no rebuild; changed Node version => native invalid only; changed DSH version => DSH/native/UI invalid; changed runtime schema => all dependent layers invalid.
- [ ] **Step 2: Run** `gradle testDebugUnitTest --tests '*BootstrapLayersTest'` and confirm failures.
- [ ] **Step 3: Implement** immutable fingerprints and marker parsing/writing with exact schema/version fields.
- [ ] **Step 4: Re-run** the targeted tests and confirm pass.
- [ ] **Step 5: Commit** with `feat(android): add layered bootstrap markers`.

### Task 2: Preserve caches and split reset semantics

**Files:**
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/runtime/RuntimeInstaller.kt`
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/HarnessForegroundService.kt`
- Test: `android/app/src/test/java/io/leostrange/dshandroid/runtime/BootstrapLayersTest.kt`

**Interfaces:**
- Produces: `clearHarnessLayers()` and `clearFullRuntime()` semantics.
- Consumes: marker state from Task 1.

- [ ] **Step 1: Add failing tests** asserting Harness reinstall leaves Termux package cache, npm cache and runtime marker intact while invalidating DSH/native/UI markers.
- [ ] **Step 2: Run** targeted unit tests and confirm failure.
- [ ] **Step 3: Implement** non-destructive Harness reinstall and separate full runtime reset path.
- [ ] **Step 4: Ensure** `RuntimeInstaller.clear()` is no longer called for ordinary Harness reinstall.
- [ ] **Step 5: Re-run** tests and commit `perf(android): preserve runtime caches across harness reinstall`.

### Task 3: Cache node-gyp headers and skip redundant native builds

**Files:**
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/HarnessForegroundService.kt`
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/runtime/NativeBuildConfig.kt`
- Test: `android/app/src/test/java/io/leostrange/dshandroid/runtime/NativeBuildConfigTest.kt`

**Interfaces:**
- Produces: deterministic native fingerprint from Node version + DSH version + `aarch64-linux-android30` + patch schema.

- [ ] **Step 1: Add failing tests** for native fingerprint changes and unchanged-node header reuse.
- [ ] **Step 2: Run** targeted tests and confirm failure.
- [ ] **Step 3: Implement** header existence/version validation under `.cache/node-gyp/<node-version>` and skip `node-gyp install` when valid.
- [ ] **Step 4: Skip** `npm rebuild` entirely when native marker validates and `koffi`/`pty.node` validation succeeds.
- [ ] **Step 5: Run** tests and commit `perf(android): reuse node headers and native artifacts`.

### Task 4: Resolve and seed Harness Internal Testing Notice acknowledgement

**Files:**
- Create: `android/app/src/main/java/io/leostrange/dshandroid/runtime/HarnessOnboarding.kt`
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/HarnessForegroundService.kt`
- Test: `android/app/src/test/java/io/leostrange/dshandroid/runtime/HarnessOnboardingTest.kt`

**Interfaces:**
- Produces: `HarnessOnboarding.inspect(dshRoot)` and `HarnessOnboarding.applyAcknowledgement(dshRoot, home, version)`.
- Consumes: installed DSH files/package metadata only; must not guess a universal key without finding evidence in the installed package.

- [ ] **Step 1: Add a failing fixture-driven test** using a minimal extracted Harness JS snippet containing the notice text and its storage/settings key, asserting the resolver returns the correct acknowledgement key/value and version scope.
- [ ] **Step 2: Run** the targeted test and confirm failure.
- [ ] **Step 3: Implement** a bounded text scan under installed DSH web/server assets for `Internal Testing Notice` and nearby persistence logic (`localStorage`, config key, cookie, settings endpoint or server-side state).
- [ ] **Step 4: Implement** only the discovered mechanism; record an onboarding marker containing DSH version + discovered key/schema. If no supported mechanism is found, emit a precise diagnostic and stop before WebView.
- [ ] **Step 5: Re-run** tests and commit `fix(android): acknowledge harness internal testing notice`.

### Task 5: Fast-path service startup

**Files:**
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/HarnessForegroundService.kt`
- Test: `android/app/src/test/java/io/leostrange/dshandroid/runtime/BootstrapLayersTest.kt`

**Interfaces:**
- Consumes: Tasks 1–4 marker evaluators.
- Produces: startup flow that verifies and reuses valid layers.

- [ ] **Step 1: Add failing tests** for startup decision order: runtime valid + DSH valid + native valid + UI valid => no download/install/rebuild.
- [ ] **Step 2: Run** tests and confirm failure.
- [ ] **Step 3: Refactor** startup into clear phases: runtime verify/install, DSH verify/install, native verify/rebuild, onboarding verify/apply, launch.
- [ ] **Step 4: Add log messages** such as `Runtime: cached`, `DSH: cached`, `Native modules: cached`, `Harness notice: acknowledged` so the UI never looks frozen.
- [ ] **Step 5: Run** tests and commit `perf(android): add fast startup path`.

### Task 6: UI recovery controls and progress wording

**Files:**
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/MainActivity.kt`
- Modify: `android/app/src/main/java/io/leostrange/dshandroid/HarnessRuntimeState.kt`

**Interfaces:**
- Produces: ordinary retry, `Reinstall Harness` and separate `Reset embedded runtime` actions.

- [ ] **Step 1: Update state/action wiring** so ordinary reinstall does not erase runtime/toolchain caches.
- [ ] **Step 2: Add** a separate full-reset action guarded by explicit user tap.
- [ ] **Step 3: Ensure** progress text identifies the current layer and whether it is reused or rebuilt.
- [ ] **Step 4: Run** `gradle testDebugUnitTest`.
- [ ] **Step 5: Commit** `feat(android): separate harness reinstall from runtime reset`.

### Task 7: CI verification and APK artifact

**Files:**
- Modify as needed: `.github/workflows/android-build.yml`

**Interfaces:**
- Produces: debug APK artifact from `temp` only.

- [ ] **Step 1: Ensure CI reconstructs the latest Android source and applies the new source files without regex-based Kotlin generation where avoidable.**
- [ ] **Step 2: Run** full `gradle testDebugUnitTest --stacktrace` in CI.
- [ ] **Step 3: Run** `gradle assembleDebug --stacktrace` in CI.
- [ ] **Step 4: Download** the artifact and verify `unzip -t app-debug.apk` returns no errors.
- [ ] **Step 5: Report** APK path, SHA-256, and explicitly note that device runtime/UI verification is still required.
