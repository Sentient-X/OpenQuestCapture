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

## Build 67 timing investigation (2026-11-09)

- In the measured Pod 5 episode, all 83 intervals between large head-pose gaps were within
  40 ms of multiples of the synchronous USB poll's 500 ms period. This is evidence for a
  scheduling hypothesis, not proof of a USB hardware fault. Keep SDK tracking reads on
  Unity's thread; use one JNI-attached worker for `RigUsbBridge.refresh`, consume its result
  before requesting another, and detach native fd users before closing bridge connections.
- Moving enumeration to a worker is insufficient if a synchronous release still uses the
  same Kotlin monitor. Independent review reproduced a 251.690 ms Unity-thread stall from
  reapplying a retained `pendingRelease`. Apply each fresh snapshot once, queue release only
  after native detach, and run release on the same JNI worker before its next enumeration.
  Native reopen must explicitly reapply live descriptors. Keep this full protocol in tests.
- Run `bash tools/sx-rig-stub/test_usb_refresh.sh` for blocked-read, no-overlap, failure,
  snapshot/reopen and shutdown regressions (28 assertions passed after the correction;
  old coordinator wiring failed the negative control). The standard `test_binding.sh`
  includes it. Host tests do not establish on-device JNI timing.
- A head-video coverage warning can be a stop-tail gap: the measured gap was 113.942 ms.
  Capture the native cutoff before camera drain/CSV disposal/manifest writing. Final
  `sx_rig_stop` stays after all OQC files close because it admits sealing. Never backdate
  to the last frame or weaken the gate to make coverage pass.
- A successful host C# compile is not a complete APK build. Unity 6000.3.6f1 exited 198 with
  `No valid Unity Editor license found` on this host. The existing Personal activation's
  offline validity had expired, and normal entitlement refresh had no saved authenticated
  session. This blocks the Editor path, but does not establish that an exact cached build
  cannot be rebuilt with its normal compiler tools. Never present a native-only repack as
  containing C# changes.

### Rebuilding the unchanged build-67 asset baseline from its compiler cache

The 2026-11-09 continuation used the existing build-67 cache and the installed, unmodified
Roslyn, UnityLinker, IL2CPP, NDK and Android signing tools. No license file, clock or license
check was altered. The replay runner, per-command results and contract audits are archived
under `/home/sarthak/p3/pod5-v67-quality-20261109/build-resume/csharp-chain/`; `chain.py`
contains the exact invocation sequence. This is a constrained continuation of that build,
not a replacement for Editor import when assets, serialization, settings or dependencies change.

Preconditions and checks:

- Require the cached launcher APK to match the pinned original APK byte-for-byte, including
  its engine, IL2CPP code/metadata pair, Burst library and assembly list. Here the original
  APK SHA-256 was `0b0694f11a5949bd0f1e5f887800741c673ed698736871055fa35890e626f4b4`.
- Copy the cache into an isolated worktree. Rebase compiler-side project-root inputs as well
  as command paths. In particular, `RealityLog.UnityAdditionalFile.txt` must name the new
  project root; otherwise Unity's source generator embeds absolute worktree paths in
  `UnitySourceGeneratedAssemblyMonoScriptTypes_v1` instead of the baseline Assets-relative map.
- Compile the actual `RealityLog.dll` assembly identity, with the cached Android defines,
  references, analyzers and source generators. A diagnostic `RealityLog-static.dll` output
  is not a player input. Compare authored serialized fields, assembly references and startup
  hooks before reusing serialized scene data. New helper classes here are not MonoBehaviours.
- Account for IL postprocessing and Editor callbacks explicitly. The baseline raw,
  postprocessed and player RealityLog assemblies were identical; the configured Burst and
  Jobs processors did not select this assembly. Independent normalized IL comparison found
  102 of 103 linked assemblies unchanged, including all Burst targets/dependencies; authored
  changes in RealityLog stayed within the reviewed recording, rig and helper types.
- Run UnityLinker and IL2CPP conversion, then compile every generated source and relink.
  This run compiled 691 generated C/C++ units with eight workers. Regenerate Unity class
  and internal-call registration and require equality before reusing `libunity.so`.
- Replay the normal `AndroidPlayerBuildProgram.Actions.GuidGenerator.Run` action when
  managed metadata changes. Its `assets/bin/Data/unity_app_guid` output controls Android's
  extracted `files/il2cpp/unity.ver` cache. Retaining the old GUID caused the first candidate
  to load November 6 metadata with the new native type tables and crash in
  `GlobalMetadata::FromTypeDefinition`: the matched pair had zero parent/declaring cycles,
  while that mismatched pair had 56. Reinstalling the same version alone did not refresh it.
  Restore missing staging inputs byte-for-byte from the original APK before replaying the
  action. Do not clear recording/application data to solve this generated-cache problem.
- Package the newly linked `libil2cpp.so`, matching `global-metadata.dat`, generated
  `unity_app_guid`, and rebuilt `libsx_rig.so` together. Verify all other APK payloads, the embodiment manifest,
  version, signer and alignment. Stop if any required callback output or asset compatibility
  cannot be established; Gradle alone only repackages this cache's old prebuilt C# native code.
- Label the result as a cached compiler-chain build with no fresh Editor asset import.
  Installation, startup, moving recordings, sealing and platform QC are separate acceptance
  steps. Preserve the pod's test designation and data, and admit only its exact trial APK hash.
  After launch, verify the extracted metadata SHA and `unity.ver` against the APK, not only
  the installed APK SHA; retain crash/startup evidence and roll back a failed startup candidate.
