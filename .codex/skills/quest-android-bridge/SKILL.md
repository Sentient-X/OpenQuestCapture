---
name: quest-android-bridge
description: Maintain the Unity-to-Android camera bridge and Quest plugin integration for OpenQuestCapture. Use when changing AndroidJava interop classes/method names, camera permission flow, Android manifest or Gradle templates, QuestCameraLib AAR updates, or the rebuild_kotlin_library.ps1 workflow.
---

# Quest Android Bridge

Use this skill to keep C# Android interop, Quest permissions, and AAR packaging aligned across Unity and QuestCameraLib.

## Read These Files First

- `Assets/RealityLog/Scripts/Runtime/Camera/CameraPermissionManager.cs`
- `Assets/RealityLog/Scripts/Runtime/Camera/CameraSessionManager.cs`
- `Assets/RealityLog/Scripts/Runtime/Camera/ImageReaderSurfaceProvider.cs`
- `Assets/Plugins/Android/AndroidManifest.xml`
- `Assets/Plugins/Android/mainTemplate.gradle`
- `Assets/Plugins/Android/gradleTemplate.properties`
- `rebuild_kotlin_library.ps1`
- `.gitmodules`

## Preserve Bridge Invariants

- Keep C# Java class/method constants synchronized with the Kotlin library API.
- Keep required permissions in Android manifest:
  - `android.permission.CAMERA`
  - `horizonos.permission.HEADSET_CAMERA`
  - `android.permission.WRITE_EXTERNAL_STORAGE`
- Keep Quest-specific activity/category metadata intact unless explicitly changing launch behavior.
- Keep `questcameralib.aar` replacement process deterministic and repeatable.

## Execute Change Workflow

1. Confirm submodule availability:
  - `git submodule status`
  - If `QuestCameraLib` is uninitialized, initialize before relying on Kotlin source changes.
2. Apply C# bridge changes first and keep constant names explicit.
3. Apply Android manifest/gradle template changes only when required for runtime behavior.
4. If AAR behavior changes, rebuild and replace plugin:
  - `pwsh ./rebuild_kotlin_library.ps1`
  - If PowerShell is unavailable, run `QuestCameraLib` gradle build directly and copy AAR to `Assets/Plugins/Android/questcameralib.aar`.
5. Re-check Unity-side permission flow and camera startup path after edits.

## Verification Checklist

- Run targeted static checks:
  - `rg -n "AndroidJavaObject|Call\\(|CAMEAR_|REQUEST_CAMERA_PERMISSION|OPEN_CAMERA|captureNextFrame" Assets/RealityLog/Scripts/Runtime/Camera`
  - `rg -n "uses-permission|supportedDevices|UnityPlayerGameActivity" Assets/Plugins/Android/AndroidManifest.xml`
  - `rg -n "android.useAndroidX|appcompat|kotlinx-serialization" Assets/Plugins/Android/mainTemplate.gradle Assets/Plugins/Android/gradleTemplate.properties`
- Confirm plugin artifact presence:
  - `ls -lh Assets/Plugins/Android/questcameralib.aar`
- If build tooling is unavailable, explicitly report skipped Android build verification.

## Definition of Done

- Keep Unity C# bridge and Kotlin/AAR interfaces in sync.
- Keep required Quest Android permissions and manifest metadata valid.
- Keep AAR update workflow reproducible.
- Report any skipped build/runtime checks with reason.
