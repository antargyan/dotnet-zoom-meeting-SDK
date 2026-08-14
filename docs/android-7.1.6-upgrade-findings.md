# Android 7.1.6 upgrade: findings and open issue

Status as of this writing: the binding compiles, the SDK **initializes** on-device, but
**joining a meeting crashes** inside Zoom's own native code. This document is the record of what
was tried, what's ruled out, and what's still open — so the next person (or session) doesn't repeat
the investigation from zero.

## Summary

| Stage | Result |
|---|---|
| Binding compiles against `mobilertc.7.1.6.41900.aar` | ✅ 0 errors |
| Public `us.zoom.sdk` API surface | ✅ 298 types (was 240 at 6.1.1), all entry points present |
| App builds and installs on device (Nokia G21, Android 13, arm64) | ✅ |
| `ZoomSDK.Initialize` | ✅ `OnZoomSDKInitializeResult errorCode=0` |
| `JoinMeetingWithParams` | ✅ returns 0 (accepted) |
| Meeting actually joins | ❌ process crashes ~500ms after join is accepted |

## The crash

```
signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0000000000000000
  #00 libzVideoApp.so
  #01 libzVideoApp.so
  #02 libzVideoApp.so
  #03 libzVideoApp.so
  #04 libzVideoApp.so
  #05 libzLoader.so
  #06 libzLoader.so  Android_InitConfModule4SingleProcess(char*, int, int, char const* const*, bool, bool, bool, bool)+56
  #07 libzLoader.so  Java_us_zoom_component_sdk_loader_jni_ZmMainboardNative_initConfModule4SingleProcessImpl+484
  #08 art_jni_trampoline
  ...
  #33 libzReflection.so  ConfProcessMgrReflection::CreateConfProcess(int&, char const*)+252
  #35-44 libzPTApp.so
```

Null-pointer dereference (fault addr `0x0`) inside Zoom's own closed-source native library, reached
via JNI from `Mainboard.initConfModule4SingleProcess`, itself invoked reflectively via
`ConfProcessMgrReflection::CreateConfProcess`. No managed code, no JNI marshalling, and no code in
this repo appears anywhere in the backtrace.

**This exact crash — same function, same offset pattern — was independently reported by the RioConf
project** (a separate MAUI app with an entirely different AndroidX/Compose dependency graph), which
concluded 7.x "does not run" and fell back to a 6.4.1 binding. Reproducing it here, with Zoom's own
declared dependency versions rather than RioConf's raised ones, is evidence against their
version-skew theory and shifts the likely cause toward Zoom's SDK itself (possibly for this
device/Android-13 combination, or for single-process mode generally).

## What actually blocked initialize (fixed)

Two real, confirmed bugs were on the path to `initialize` succeeding at all. Both are still relevant
if this binding is touched again:

### 1. Missing `coil-base` — `ClassNotFoundException: coil.ImageLoader$Builder`

Zoom 7.1.6 constructs a Coil `ImageLoader` inside `ZoomSDK.initialize`. The repo's existing
`Binding.Io.CoilKt.CoilCompose` 2.0.0 NuGet package declares **zero dependencies**, so it never pulls
`coil-base`, and `coil.ImageLoader` lives in coil-base.

Fix: vendored `coil-base-2.3.0.jar` and `coil-2.3.0.jar` (Zoom's declared `coilVersion`) into
`src/MAUI/CoilJars/`, referenced via `<AndroidLibrary Include="..\CoilJars\...jar" Bind="false" />`
in `SampleApp.csproj`.

**Why jars, not `AndroidMavenLibrary` or a NuGet binding**, in case someone tries those again:
- `Io.CoilKt.Coil`/`CoilBase` 2.0.0.3 (NuGet) resolve their Java deps via the `Dependencies.Gradle`
  package, which failed here with `XACDJ7028` (a `kotlin-android-extensions-runtime` jar never
  populated in `~/.gradle/caches`).
- `<AndroidMavenLibrary Include="io.coil-kt:coil-base" ... Bind="false" />` resolves from Maven fine,
  but staging it as an aar creates an `lp/<n>.stamp` marker that aapt2 is then handed as a resource
  path, failing with `APT2144: invalid file path '...stamp'`.
- A jar placed *inside* a project folder is picked up by default `@(AndroidLibrary)` globbing and
  **bound** regardless of `Bind="false"`, generating a managed Coil API that doesn't compile
  (`CrossfadeDrawable`, `GenericViewTarget`...). Hence `CoilJars/` sits outside every project
  directory.
- `coil-compose`/`coil-compose-base` were deliberately **not** added: their dependency
  `accompanist-drawablepainter`'s only current Xamarin binding (0.37.3.x) requires **Compose UI
  1.11.3.1**, which would drag the whole Compose graph off Zoom's declared 1.9.4 line — the exact
  skew RioConf blames for instability. If a Zoom Compose screen ever needs `AsyncImage`, that
  needs an accompanist build on Compose 1.9.x, which may not exist yet.

**Consumers of `zoommeetingsdk.dotnet.android` need this too** — jar references don't flow through a
NuGet package.

### 2. Coroutines version mismatch — `Module with the Main dispatcher is missing`

```
IllegalStateException: Module with the Main dispatcher is missing. Add dependency providing the
Main dispatcher, e.g. 'kotlinx-coroutines-android' and ensure it has the same version as
'kotlinx-coroutines-core'
```

The AndroidX/Compose graph pulls `KotlinX.Coroutines.Core.Jvm` transitively to **1.10.2.1**
(`Compose.Runtime`, `Lifecycle.*`, `Activity`, `AndroidX.Core` all require it — the actual coroutine
core classes ship in that Jvm artifact). The explicit `Coroutines.Android`/`Core` pins were still at
Zoom's declared **1.9.0.4**, so the runtime had mismatched core/android artifacts and the Main
dispatcher factory never registered.

Fix: aligned `Xamarin.KotlinX.Coroutines.Android` and `.Core` to **1.10.2.1** in both
`MobileRTC.Android.csproj` and `SampleApp.csproj`. This is a deliberate departure from Zoom's
declared 1.9.0 — justified because Zoom declares it as a floor (Gradle would resolve upward too),
and the mismatch is fatal rather than cosmetic.

## What was ruled out for the join crash

Each of these was tested and disproven — recorded so they aren't retried:

| Hypothesis | Test | Result |
|---|---|---|
| R8/ProGuard stripping reflectively-used classes | Added Zoom's own `proguard.cfg` keep rules (`zoom-proguard.cfg`) | Regressed init (brought back the coroutines error); reverted |
| R8 shrinking in general | Disabled Java shrinking entirely (`AndroidLinkTool` empty) | **Identical crash**, byte-for-byte same offsets — proves shrinking is not the cause |
| Runtime permissions not granted | Granted `CAMERA`, `RECORD_AUDIO`, `READ_PHONE_STATE`, `BLUETOOTH_CONNECT` etc. via `adb shell pm grant` | Identical crash |
| Missing `ZoomSDKInitParams.Domain`/`EnableLog` | Already set to `"zoom.us"` / `true` in `ZoomSDKService.Android.cs` (matches Zoom's own sample) before this test | Crashes anyway |
| Missing native libraries / wrong ABI | Diffed all `.so` in the aar against the built APK | All 84 arm64-v8a libs present; device is arm64 |
| Missing/dropped manifest components | Diffed all `us.zoom.*`/`com.zipow.*` manifest entries (aar vs merged APK) | All 37 present; legacy manifest merger works correctly |
| Stripped coroutines `ServiceLoader` metadata | Checked `META-INF/services/kotlinx.coroutines.*` in the built APK | Present and correct |
| Missing `BROADCAST_STICKY` permission (only real gap vs Zoom's sample) | N/A — too minor to explain a native SIGSEGV | Not pursued further |

## The decisive test (in progress / next step)

Built Zoom's own **unmodified** sample app (`mobilertc-android-studio/sample`, from the 7.1.6.41900
SDK download) with plain Gradle 8.11.1 + JDK 21 — no .NET, no MAUI, no binding project involved at
all. This isolates whether the crash is:
- **A defect in Zoom's SDK** for this device/Android version (if their own sample also crashes), or
- **Something in .NET Android's packaging** that genuinely differs from Gradle's (if their sample
  works).

Build notes if repeating this:
- `local.properties` needs `sdk.dir=C:\Program Files (x86)\Android\android-sdk` (escaped backslashes)
  — get this wrong and Gradle fails with a cryptic `IOException: filename... syntax is incorrect`
  many tasks deep, not at the manifest step.
- Run from PowerShell with native Windows paths for `JAVA_HOME`/`ANDROID_HOME` — bash's
  forward-slash `/c/Program Files/...` form appears to trip some path handling inside Gradle on
  Windows.
- `us.zoom.sdksample.initsdk.AuthConstants.SDK_JWTTOKEN` is a **hardcoded compile-time constant**,
  not a runtime UI field — the sample has no JWT entry screen. Set it and rebuild
  (`./gradlew.bat :sample:assembleDebug`, ~13 minutes cold, faster once Gradle's cache is warm) to
  test with a real token.
- First `packageDebug` run failed with a vague `IncrementalSplitterRunnable` failure; a bare retry of
  the same command succeeded. Possibly a transient lock/cache issue — not investigated further.

**Whoever picks this up next: fill in the JWT, rebuild, install `sample-debug.apk` alongside this
repo's `SampleApp`, join the same meeting, and check whether the identical `SIGSEGV` at
`Android_InitConfModule4SingleProcess+56` occurs.**

## If it's a genuine Zoom SDK defect

- File a support ticket with Zoom. This report is unusually strong evidence: two independent
  dependency graphs, both hitting the identical native function and offset pattern.
- Cite: SDK version `7.1.6.41900`, device (Nokia G21 / `ShadowcatPlus_00WW`, Android 13,
  `TP1A.220624.014`), the exact backtrace above, and — once run — whether Zoom's own sample
  reproduces it.
- Use the format the [dev forum FAQ](https://devforum.zoom.us/t/please-read-before-post-troubleshooting-tips-for-zoom-mobile-sdks-faq/4366)
  asks for: description, SDK version, reproducible steps, screenshots, device info, and error
  logs/crash analytics. That thread is a submission template, not a troubleshooting checklist — it
  has no technical content to check against.

### External research already done (don't repeat this)

- **[Get Started docs](https://developers.zoom.us/docs/meeting-sdk/android/get-started/)**: the one
  native-packaging-relevant line is *"To reduce your app size, include `useLegacyPackaging = true`"*
  in `build.gradle`. Checked directly by diffing our built APK against Zoom's own Gradle-built sample
  APK (`unzip -v`, `aapt2 dump xmltree`): both use `Defl:N` compression for every `.so` and both set
  `android:extractNativeLibs="true"`. Identical — ruled out.
- **Forum/GitHub search** for the exact crash signature (`Android_InitConfModule4SingleProcess`,
  `ConfProcessMgrReflection`) turned up no exact match. Closest related reports, and why none apply:
  - A 5.7.1-era `ClassNotFoundException` from missing ProGuard keep rules
    ([thread](https://devforum.zoom.us/t/zoom-android-sdk-working-in-debug-apk-but-signed-apk-crashing-when-zoom-activity-start/67433)),
    staff-confirmed fix = the same `proguard.cfg` keep rules already tried here (see above). Doesn't
    match: that crash is a managed exception; ours is a pure native SIGSEGV that persists with R8
    shrinking fully disabled.
  - A Flutter-wrapper crash on Android 12+, `SecurityException` from missing `READ_PHONE_STATE`
    ([flutter_zoom_sdk#65](https://github.com/evilrat/flutter_zoom_sdk/issues/65)). Doesn't match: we
    granted every runtime permission and got the identical native crash anyway.
  - An AAB/dynamic-module `ClassNotFoundException` at Intent unmarshalling, staff-acknowledged but
    unresolved ([thread](https://devforum.zoom.us/t/android-dynamic-module-crashes-upon-joining-meeting/112969)).
    Doesn't match: that's specific to Android App Bundle / Play Feature Delivery packaging; we're
    testing a plain APK.
  - Several other join/init crash threads exist but are older SDK versions (5.x) with different
    (managed-exception) symptoms, not this native signature.
- **Conclusion**: this specific crash does not appear to be publicly documented or previously
  reported to Zoom in a way that surfaced in search. That's evidence it's either rare or specific to
  this exact combination (device/Android 13/SDK 7.1.6/single-process mode) — worth stating plainly in
  the support ticket rather than assuming Zoom staff will recognize it.

## Fallback

RioConf's separate **6.4.1** binding (already upgraded to net10) is known-good at runtime. If 7.1.6
turns out to be unusable on Android for the foreseeable future, shipping 6.4.1 is a working outcome
— check it still meets Zoom's [minimum supported version](https://developers.zoom.us/docs/meeting-sdk/minimum-version)
policy before committing to that path.

## Unrelated but real blocker: package size

`zoommeetingsdk.dotnet.android` now packs to **~316 MB**, over nuget.org's 250 MB hard limit. This
needs a distribution decision independent of whether the crash above gets resolved.

## Other loose ends from this pass

- `blueparrottsdk`, `constraintlayout-compose`, `coil-gif`, and MLKit text-recognition are declared
  by Zoom's `versions.gradle`/`build.gradle` but have no usable Xamarin binding. Documented inline in
  `MobileRTC.Android.csproj`; features depending on them will fail at runtime if exercised.
- Several 6.1.1-era `Transforms/Metadata.xml` rules were keyed to Zoom's *obfuscated* internal class/
  interface names (e.g. `us.zoom.proguard.x60`). Those names are regenerated every Zoom release, so
  such rules silently stop matching (`BG8A00: matched no nodes`) without failing the build — this is
  what caused the entire public `MeetingService`/`InMeetingService` API to vanish from the 7.1.6
  binding until traced down. Prefer matching on stable signals (package prefixes, parameter shapes)
  over obfuscated names where possible; see the `us.zoom.proguard.` prefix rule in `Metadata.xml`
  for the pattern used to fix it.
