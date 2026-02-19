---
name: capture-pipeline
description: Maintain synchronized camera, depth, and pose capture in OpenQuestCapture. Use when changing recording lifecycle, timestamp alignment, capture FPS timing, session directory naming, or runtime capture code in Assets/RealityLog/Scripts/Runtime/{RecordingManager,Common,Camera,Depth,OVR}.
---

# Capture Pipeline

Use this skill to change Quest capture runtime behavior without breaking cross-sensor synchronization or output file layout.

## Read These Files First

- `Assets/RealityLog/Scripts/Runtime/RecordingManager.cs`
- `Assets/RealityLog/Scripts/Runtime/Common/CaptureTimer.cs`
- `Assets/RealityLog/Scripts/Runtime/Depth/DepthMapExporter.cs`
- `Assets/RealityLog/Scripts/Runtime/Camera/ImageReaderSurfaceProvider.cs`
- `Assets/RealityLog/Scripts/Runtime/OVR/PoseLogger.cs`
- `Assets/RealityLog/Scripts/Runtime/Camera/CameraSessionManager.cs`
- `Assets/RealityLog/Scenes/RealityLogScene.unity` (wiring and serialized defaults)
- `README.md` (expected output layout and capture contract)

## Preserve Runtime Invariants

- Generate one session directory per recording start in `RecordingManager`.
- Propagate the same session directory to depth, camera providers, and pose loggers before starting capture.
- Keep synchronized trigger flow:
  - Set cadence in `CaptureTimer.Update`.
  - Read trigger in `ImageReaderSurfaceProvider.LateUpdate`.
  - Read trigger in `DepthMapExporter.OnBeforeRender`.
- Reset base time anchors at recording start for depth and pose logging.
- Keep output naming and folder structure stable unless a migration is explicitly requested.
- Keep `OnApplicationPause` camera close/reopen logic in `CameraSessionManager` intact.

## Execute Change Workflow

1. Inspect impacted subsystem and list which invariant is touched.
2. Modify the smallest set of runtime files needed.
3. Update scene wiring only if a new serialized field is introduced.
4. Keep logs actionable and component names unchanged unless there is a functional reason.
5. Re-check timestamp conversion paths and session path propagation after edits.

## Verification Checklist

- Run targeted static checks:
  - `rg -n "ShouldCaptureThisFrame|StartCapture|StartRecording|StopRecording|ConvertTimestamp|DataDirectoryName" Assets/RealityLog/Scripts/Runtime`
  - `rg -n "left_depth|right_depth|left_camera_raw|right_camera_raw|hmd_poses.csv" README.md Assets/RealityLog/Scripts/Runtime`
- If Unity CLI is available, run a batch compile pass:
  - `"$UNITY_PATH" -batchmode -projectPath "$PWD" -quit -logFile /tmp/openquestcapture-unity.log`
  - `rg -n "error CS|Exception|Assertion" /tmp/openquestcapture-unity.log`
- If Unity CLI is unavailable, explicitly report that runtime compilation was not executed.

## Definition of Done

- Preserve synchronized camera/depth/pose capture behavior.
- Preserve session folder layout and file naming contract.
- Keep scene references valid for changed serialized fields.
- Document skipped runtime checks if local tooling is unavailable.
