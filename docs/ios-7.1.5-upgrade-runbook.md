# iOS binding upgrade runbook: MobileRTC 6.1.0.16236 → 7.1.5.37603

The Android side of this SDK bump is done and build-verified. iOS is **not** started, because
regenerating `ApiDefinitions.cs` requires Objective Sharpie, which only runs on macOS.

This document is the handover: everything already worked out, so the Mac session is mechanical.

## Why this can't be done on Windows

`ApiDefinitions.cs` (~5,100 lines) and `StructsAndEnums.cs` are *generated* from the framework's
Objective-C headers by Objective Sharpie, then hand-patched. The delta in this upgrade is large:

| Measure | Value |
|---|---|
| Shared headers that changed | **69 of 69** (all of them) |
| Changed header lines | **~15,500** |
| New headers | **7** |

New headers: `MobileRTCAppSignalPanelHandler.h`, `MobileRTCCustom3DAvatarElementSettingContext.h`,
`MobileRTCFaceROIInfo.h`, `MobileRTCJoinMeetingInfoHandler.h`, `MobileRTCMeetingService+Docs.h`,
`MobileRTCMeetingSettings+Custom3DAvatar.h`, `MobileRTCVideoPixelBufferExtraInfo.h`.

Highest-churn existing headers (changed lines): `MobileRTCMeetingDelegate.h` (2011),
`MobileRTCConstants.h` (1863), `MobileRTCMeetingService+InMeeting.h` (881),
`MobileRTCMeetingChat.h` (822), `MobileRTCMeetingSettings.h` (755), `MobileRTCBORole.h` (564).

Hand-porting that volume would compile but could not be link- or run-verified from Windows, and a
wrong selector or argument type surfaces only as a runtime crash on device. Sharpie reads the
headers directly and gets this right.

## Prerequisites on the Mac

- Xcode **26** (the iOS 26 SDK — `net10.0-ios` in this repo targets `ios26.0`; check with
  `xcodebuild -showsdks`)
- .NET **10** SDK + `ios` workload: `dotnet workload install ios`
- Objective Sharpie: https://aka.ms/objective-sharpie (`sharpie --version` to confirm)

## Step 1 — drop in the new native assets

From the extracted `zoom-sdk-ios-7.1.5.37603`:

```bash
cd src/MAUI/iOS/MobileRTC.iOS
rm -rf MobileRTC.xcframework Resources/MobileRTCResources.bundle
cp -R /path/to/zoom-sdk-ios-7.1.5.37603/lib/MobileRTC.xcframework .
cp -R /path/to/zoom-sdk-ios-7.1.5.37603/lib/MobileRTCResources.bundle Resources/
```

**Simulator slice change:** 6.1.0 shipped `ios-arm64_x86_64-simulator`; 7.1.5 ships
`ios-arm64-simulator` only. Intel Macs can no longer run the simulator — Apple Silicon only.

Note `Resources/MobileRTCResources.bundle` is currently **not** referenced by
`MobileRTC.iOS.csproj` (no `BundleResource` items), so it ships to nobody. Consuming apps have to
add the bundle themselves. Worth fixing while you're in here, but it is pre-existing behaviour, not
a regression.

## Step 2 — run Objective Sharpie

```bash
sharpie bind -sdk iphoneos26.0 -namespace Zoomios -scope MobileRTC.xcframework/ios-arm64/MobileRTC.framework/Headers MobileRTC.xcframework/ios-arm64/MobileRTC.framework/Headers/MobileRTC.h -o sharpie-out -c -F MobileRTC.xcframework/ios-arm64 -ObjC
```

`-namespace Zoomios` matters: the existing binding lives in namespace `Zoomios` and consuming code
imports that. Do not let it default to `MobileRTC`.

## Step 3 — merge, don't blind-overwrite

Sharpie writes `ApiDefinitions.cs` + `StructsAndEnums.cs` into `sharpie-out/`. Diff rather than
replace, because the checked-in files carry hand fixes that Sharpie will not reproduce:

1. **`Handle` name collisions — this one is known to bite.** During the .NET 10 upgrade,
   `MobileRTCReminderDelegate`'s `void Handle(...)` had to be renamed to `OnReminderNotify(...)`
   ([ApiDefinitions.cs](../src/MAUI/iOS/MobileRTC.iOS/ApiDefinitions.cs), search
   `onReminderNotify`). On .NET 10 the generator emits protocol members as default interface
   members, so any member named `Handle` collides with `INativeObject.Handle` and fails with
   **CS1503: cannot convert from 'method group' to 'nint'**. Sharpie will regenerate it as `Handle`
   from the `handle:` selector part — rename it again, keeping `[Export ("onReminderNotify:handle:")]`
   untouched. Apply the same fix to any *new* member Sharpie names `Handle`.
2. **`[Verify]` attributes.** Sharpie emits these where it guessed. The existing file has 2, both
   commented out (around line 547). Every `[Verify]` in new output must be checked against the
   header and then removed — they are compile errors by design.
3. **`[Protocol, Model]` + `[BaseType (typeof(NSObject))]`** is the established pattern for the
   delegate protocols in this binding; keep it consistent with what is already checked in.
4. Keep the explanatory header comment block at the top of `ApiDefinitions.cs`.

## Step 4 — version bumps

In [MobileRTC.iOS.csproj](../src/MAUI/iOS/MobileRTC.iOS/MobileRTC.iOS.csproj):

```xml
<Version>7.1.5.37603</Version>
```

`TargetFrameworks` (`net10.0-ios`) and `SupportedOSPlatformVersion` (15.0) are already correct from
the .NET 10 upgrade — but confirm 7.1.5 still supports iOS 15; if Zoom raised its floor, raise this
to match. Then update the iOS version line and the `iOSMAUINugetLink` in [README.md](../README.md).

## Step 5 — verify

```bash
dotnet build src/MAUI/iOS/MobileRTC.iOS/MobileRTC.iOS.csproj -c Release
```

A clean compile only proves the definitions are *self-consistent*. Before publishing, run the
SampleApp against a real device and actually join a meeting — that is the only thing that catches a
wrong selector or a mismatched argument type.

Note the SampleApp is currently Android-only (`<TargetFrameworks>net10.0-android</TargetFrameworks>`);
add `net10.0-ios` to exercise the iOS path. The iOS `ProjectReference` and `SupportedOSPlatformVersion`
condition are already in [SampleApp.csproj](../src/MAUI/SampleApp/SampleApp.csproj), so it is a
one-line change plus whatever platform code the sample needs.
