# Android Web UI + Fast Bootstrap Design

## Goal
Make the Android build open the normal DeepSeek Harness web interface instead of the `Internal Testing Notice`, while making repeated launches and reinstalls dramatically faster.

## Architecture
The app keeps the official Harness web UI and backend. Android-specific code only prepares the embedded Termux/Node runtime, installs DSH, applies Android compatibility patches, starts `dsh web`, and hosts the result in WebView.

Bootstrap is split into independently versioned layers:
1. **Runtime layer** — Termux packages, Node.js, npm, compiler toolchain.
2. **DSH package layer** — global `@deepseek-ai/dsh` package tree.
3. **Native layer** — compiled `koffi`, `node-pty`, and Android-specific npm patches/fallbacks.
4. **UI onboarding layer** — records/sets the same acknowledgement state expected by Harness so the normal web UI is shown instead of the internal testing notice.

Each layer has its own marker containing schema/version inputs. A layer is rebuilt only when its marker no longer matches or required files fail validation.

## Web UI behaviour
The application must not replace Harness with a custom chat UI. After `dsh web` becomes reachable, WebView should load the native Harness interface including chats, session/sidebar navigation, model/provider settings, agent controls, and other functionality supplied by Harness.

Before WebView is shown, Android bootstrap determines how Harness stores acknowledgement of the `Internal Testing Notice` and seeds that state using the same storage/settings mechanism. The workaround must be version-scoped so a changed notice/version can be detected rather than blindly suppressing every future warning.

If the acknowledgement mechanism cannot be safely resolved for the installed DSH version, the app must fail with a diagnostic log entry rather than deleting or broadly patching arbitrary UI code.

## Fast bootstrap
Normal app start must not perform network downloads, npm install, node-gyp header download, or native compilation when all layer markers validate.

The runtime package cache and npm cache remain outside destructive DSH reinstall operations. `Reinstall Harness` invalidates only DSH/native/UI layers; a separate `Reset embedded runtime` action is reserved for full recovery.

Node headers downloaded by node-gyp are reused whenever the Node version is unchanged. Native modules are rebuilt only when DSH version, Node ABI/version, Android target, or native patch schema changes.

## Recovery and validation
Before starting the server, validate:
- Node and npm execute.
- DSH package metadata matches the DSH marker.
- `koffi` can be required.
- `node-pty/build/Release/pty.node` exists.
- the Android node-pty postinstall patch marker is present when needed.
- the UI onboarding acknowledgement matches the installed DSH version.

A failed layer validation invalidates only that layer and its dependants.

## UX
Progress text reports the actual layer and whether it is reused or rebuilt. Repeated startup should normally show a short verification/start sequence, not installation UI.

`Reinstall Harness` keeps the runtime and caches. A full runtime reset remains available separately for corrupted Termux/toolchain state.

## Testing
Add unit tests for marker/version decisions, dependency invalidation, and command construction. CI must run unit tests and `assembleDebug`. Device verification remains necessary for the final Harness UI because GitHub Actions cannot reproduce Android WebView/PRoot runtime behaviour.

## Constraints
- ARM64 only for now.
- Keep targetSdk 28 because writable app-private executable behaviour is required for this sideloaded architecture.
- Do not merge or push these changes to `main`; development stays on `temp` until explicitly approved.
- Preserve the existing official Harness UI rather than reimplementing it.
