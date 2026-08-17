# Android 7.1.6 upgrade: findings

Status: **solved and verified on-device.** The binding compiles, the SDK initializes, and a meeting
joins end-to-end (Zoom's real in-meeting UI, mute/video/leave controls, "Waiting for the host to
start the meeting"). This document records the root cause and everything ruled out on the way, so
nobody repeats the investigation.

## Summary

| Stage | Result |
|---|---|
| Binding compiles against `mobilertc.7.1.6.41900.aar` | OK, 0 errors |
| Public `us.zoom.sdk` API surface | OK, 298 types (was 240 at 6.1.1) |
| App builds and installs on device (Nokia G21, Android 13, arm64) | OK |
| `ZoomSDK.Initialize` | OK, `errorCode=0`, version 7.1.6 (41900) |
| `JoinMeetingWithParams` | OK, returns 0 |
| Meeting actually joins | **OK, in-meeting UI reached, 0 crashes** |

## Root cause: null `g_javaVM`, caused by .NET Android changing native library load order

The join crashed with:

```
signal 11 (SIGSEGV), code 1 (SEGV_MAPERR), fault addr 0x0
Cause: null pointer dereference
  #00-#04 libzVideoApp.so
  #05-#06 libzLoader.so  Android_InitConfModule4SingleProcess(char*,int,int,char const* const*,bool,bool,bool,bool)+56
  #07     libzLoader.so  Java_us_zoom_component_sdk_loader_jni_ZmMainboardNative_initConfModule4SingleProcessImpl+484
  ...     Mainboard.initConfModule4SingleProcess <- ZmSdkMainBoard.createConfAppForSdk
          <- VideoBoxApplication.startConfServiceForSDK <- ConfProcessMgr.createConfProcess
          <- ConfProcessMgrReflection.createConfProcess
```

Disassembling the faulting address in `libzVideoApp.so` gives the exact instruction (note frame `+56`
is the *return address* inside a thunk, not the fault site - the real code is in its callee):

```asm
adrp x22, #0x1655000
ldr  x22, [x22, #0x128]   ; GOT slot -> &g_javaVM   (R_AARCH64_GLOB_DAT -> symbol g_javaVM)
mov  w2, #4
movk w2, #1, lsl #16      ; w2 = 0x00010004 = JNI_VERSION_1_4
ldr  x0, [x22]            ; x0 = g_javaVM  == NULL
ldr  x8, [x0]             ; <-- SIGSEGV, fault addr 0x0
ldr  x8, [x8, #0x30]      ; JNIInvokeInterface::GetEnv
blr  x8                   ; GetEnv(vm, &env, JNI_VERSION_1_4); AttachCurrentThread is +0x20
```

`x2 = 0x10004` in the register dump confirms the read. Zoom's video module calls `GetEnv` on a
**null `JavaVM*`, with no null check**.

Why it is null:

- `libzVideoApp.so` does **not** define `g_javaVM` - it imports it (`SHN_UNDEF`), resolved at load
  time through the GOT.
- **Four** libraries in `mobilertc.aar` each *export* `g_javaVM` with default visibility and each set
  it from their own `JNI_OnLoad`, also via the GOT:
  `libcmmlib.so`, `libzReflection.so`, `libzoombase_shared.so`, `libzUnifyWebViewApp.so`.
- Because the symbol is preemptible and every access is GOT-mediated, **which copy a given reference
  binds to is decided purely by library load order.**
- Zoom's own loader deliberately loads `libzReflection.so` early. Verified from the working native
  sample's logcat: `libc++_shared, libusb-1.0, libuvc, libzoom_util, libzReflection, libcares, ...`
- **.NET Android 10 preloads every JNI-referenced native library at process start, in alphabetical
  order** (`dso_jni_preloads_idx` - "Indices into dso_cache[] of DSO libraries to preload because of
  JNI use" - baked into `libxamarin-app.so`; 156 entries here). Observed order:
  `libAndroidCameraBridge, libAndroidEasyIPC, libannotate, libcmmlib, libmcm, ... libzReflection`.
- So `libcmmlib` (c) won interposition instead of `libzReflection` (z), and `libzVideoApp`'s
  `g_javaVM` bound to a copy nothing initialises. Init succeeds because it never touches this path;
  the join then dereferences null during conf-module setup.

### The fix

```xml
<!-- SampleApp.csproj -->
<AndroidIgnoreAllJniPreload>true</AndroidIgnoreAllJniPreload>
```

This hands load ordering back to Zoom's own Java loader, exactly as in the working native sample.
Confirmed: afterwards the log shows `libzReflection.so` loaded first, then `libcmmlib.so`, and the
join reaches the in-meeting UI with zero crashes.

`$(AndroidIgnoreAllJniPreload)` adds every native library to `@(AndroidNativeLibraryNoJniPreload)`.
Preloading is only a startup optimisation - libraries still load on demand - so nothing is lost. A
narrower alternative is to list just the 84 Zoom `.so` names in
`@(AndroidNativeLibraryNoJniPreload)`, but that must be rechecked on every SDK bump; the global
switch is what is verified here.

**This is a consumer requirement, not just a sample-app setting.** Any app referencing this binding
needs it - see the README's Android gotchas.

### Why this also explains RioConf

RioConf (a separate MAUI app with an entirely different AndroidX/Compose dependency graph) hit the
identical crash, concluded 7.x "does not run", and fell back to a 6.4.1 binding. Both apps are .NET
Android, so both got the alphabetical preload. The crash was never about their raised dependency
versions - which is exactly why changing versions never helped.

### A second, unrelated bug this uncovered

Once the join actually worked, `ZmConfActivity` crashed with
`ClassNotFoundException: coil.decode.GifDecoder$Factory` - the in-meeting UI builds a Coil
`ImageLoader` with a GIF decoder. Zoom's sample `build.gradle` does force `libraries.coilGif`; only
`coil-base`/`coil` had been vendored here. Fixed with
`<AndroidMavenLibrary Include="io.coil-kt:coil-gif" Version="2.3.0" Bind="false" />` plus its
required `Xamarin.AndroidX.VectorDrawable.Animated` (XA4242). This failure is unreachable until the
native crash is fixed, which is why it never showed up earlier.

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

Each of these was tested and disproven before the real cause (native library load order, above) was
found — recorded so they aren't retried:

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
| Material3 too old (`shouldExecute(ZI)Z` theory, see below) + entire "working native join flow" checklist | Upgraded material3-android to Zoom's declared 1.5.0-alpha07, pinned Compose.Runtime/UI/Foundation to 1.9.4 explicitly, rewrote the join flow to fetch and attach a ZAK via `JoinMeetingParam4WithoutLogin` | **Identical crash**, same two leading `libzVideoApp.so` offsets, tested with real meeting credentials |

### A false negative worth recording

The Material3/ZAK change was first tested with placeholder meeting credentials and initially reported (wrongly) as fixing the crash — `JoinMeetingWithParams` returned `99` (`MEETING_ERROR_INVALID_ARGUMENTS`) with no crash. That was a false negative, not a fix: invalid-argument rejection is validated early and synchronously, before the native conf-process path ever runs, so that test could never have reached the crash site regardless of whether it was fixed. Retesting with real meeting credentials (`JoinMeetingWithParams returned 0`, ZAK present, join accepted) reproduced the identical crash half a second later. **Always retest against a real meeting, not just "no crash," before concluding a join-path fix worked** - a rejected join and a fixed crash look the same in logcat if you only check for the absence of `Fatal signal`.

The Material3/Compose/ZAK changes are kept regardless (see "What actually blocked initialize" and the join flow in `ZoomSDKService.Android.cs`) - they're independently correct alignment with Zoom's own declared dependency versions and the documented ZAK requirement, and may prevent a *different*, later-stage failure (the managed `NoSuchMethodError` this same reference document describes) once the native crash itself is resolved. They just aren't the fix for this crash.

## The decisive test that cracked it

Zoom's own **unmodified** sample (`mobilertc-android-studio/sample`, plain Gradle 8.11.1 + JDK 21, no
.NET/MAUI) was built and run on the same Nokia G21, joining the same meeting with the same ZAK. It
**worked** - ruling out "Zoom SDK defect on this device/Android 13/single-process mode" and pointing
at .NET Android packaging. Diffing the two running processes then produced the load-order finding
above.

Build notes if repeating this:

- `local.properties` needs `sdk.dir=C:\Program Files (x86)\Android\android-sdk` with escaped
  backslashes - get it wrong and Gradle fails with a cryptic
  `IOException: filename... syntax is incorrect` many tasks deep, not at the manifest step.
- Run from PowerShell with native Windows paths for `JAVA_HOME`/`ANDROID_HOME`.
- `sample/build.gradle` needs `packagingOptions { resources { excludes += ["**/*.aidl"] } }` - AGP's
  resource merger otherwise rejects `.aidl` source files packaged inside `mobilertc.aar`.
- `us.zoom.sdksample.initsdk.AuthConstants.SDK_JWTTOKEN` is a hardcoded compile-time constant, not a
  UI field (`./gradlew.bat :sample:assembleDebug`, ~13 min cold).
- First `packageDebug` failed with a vague `IncrementalSplitterRunnable` error; a bare retry worked.

## Diagnostic techniques that were essential

Reusable for any future native crash in this SDK:

- **`EnableGenerateDump` must be `false` to get a usable backtrace.** When `true`, the SDK installs
  its own signal handler, writes an *encrypted* `.dmp` to `/sdcard/Android/data/<pkg>/logs/` that
  only Zoom support can read, and exits - so `tombstoned` never prints a symbolised backtrace.
  `zSdkApp_0.log` in that directory is encrypted too.
- **Capture with a live `adb logcat -b all > file` running.** A post-hoc `logcat -d` repeatedly missed
  the `DEBUG`/tombstone block; tombstones under `/data/tombstones` are root-only on this device.
- **A Debug build is required for `run-as`.** A previously-installed Release APK silently failed
  `run-as` with "package not debuggable", blocking access to the app's private data dir.
- **Symbolise by hand.** Zoom's `.so` files keep only `.dynsym`, so nearest-symbol lookups land on
  unrelated names. Parse the ELF, disassemble the faulting `pc` (capstone), then resolve the data
  reference through `.rela.dyn` - that is what produced `g_javaVM`.
- **Do not trust a frame's `+offset` as the crash site** when the named function is a thunk;
  `Android_InitConfModule4SingleProcess+56` is a `bl`, i.e. a return address.
- **`pm clear` breaks a Fast Deployment Debug build** (it wipes `files/.__override__`), giving
  `No assemblies found ... Assuming this is part of Fast Deployment. Exiting...` and SIGABRT.
  Reinstall after clearing app data.
- **Compare against the working native app, mechanically.** Class sets, native `.so` sets, merged
  manifest components, resource tables and permissions were all diffed APK-to-APK and all came back
  equivalent; the load *order* was the only real difference, and only a runtime log showed it.

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

## Fallback (no longer needed)

RioConf's separate **6.4.1** binding was the fallback while 7.1.6 appeared unusable. 7.1.6 now joins
meetings, so the fallback is not required. Kept here only as context for why RioConf sits on 6.4.1.

## Unrelated but real blocker: package size

`zoommeetingsdk.dotnet.android` now packs to **~316 MB**, over nuget.org's 250 MB hard limit. This
needs a distribution decision independent of whether the crash above gets resolved.

## Other loose ends from this pass

- `blueparrottsdk` and MLKit text-recognition are declared
  by Zoom's `versions.gradle`/`build.gradle` but have no usable Xamarin binding. Documented inline in
  `MobileRTC.Android.csproj`; features depending on them will fail at runtime if exercised.
- Several 6.1.1-era `Transforms/Metadata.xml` rules were keyed to Zoom's *obfuscated* internal class/
  interface names (e.g. `us.zoom.proguard.x60`). Those names are regenerated every Zoom release, so
  such rules silently stop matching (`BG8A00: matched no nodes`) without failing the build — this is
  what caused the entire public `MeetingService`/`InMeetingService` API to vanish from the 7.1.6
  binding until traced down. Prefer matching on stable signals (package prefixes, parameter shapes)
  over obfuscated names where possible; see the `us.zoom.proguard.` prefix rule in `Metadata.xml`
  for the pattern used to fix it.
