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

### Moving tracking-gap reproduction and review queue (2026-11-12)

- Pod 5's moving baseline `20261112_174440` refused sealing on a 189.130 ms body gap
  against the unchanged 100 ms limit. HMD/controller means near 50 Hz concealed 180 gaps
  over 100 ms and 1,066 HMD callback intervals below 1 ms. Evaluate source intervals inside
  the native start/stop anchors, not only row counts or average rates. Body correctly skips
  repeated render frames; never fabricate samples to make its mean or gap pass.
- Correlate CSV MONOTONIC anchors with Perfetto clock snapshots before querying scheduler
  states. During the worst 190.469 ms host interval UnityMain executed 151.978 ms; its
  longest individual sleep was 3.052 ms. This establishes delayed polling while main-thread
  work executes, but does not identify one method. In this run no clear 500 ms phase lock
  existed. Method attribution needs instrumentation or a method-level profile.
- A hidden menu is not inactive: `RecordingMenuController` retains the active hierarchy
  and changes `CanvasGroup.alpha`. A selected Pod card can keep parsing status while hidden.
  The candidate uses one bounded background status parser shared by card/phone readers;
  SDK tracking calls remain on Unity's thread. Source and host tests alone do not prove
  that this change removes the observed gaps. Require another genuine moving test and seal.
- Run `tools/sx-rig-stub/test_binding.sh` with the actual Unity Editor references and native
  include directory. This candidate passed 644 assertions, including 45 tracking-readiness,
  13 background-snapshot and 28 review assertions. Deliberate old/broken copies fail these
  protections. Readiness rejects invalid source state at admission; it cannot guarantee
  uninterrupted tracking during an episode.
- Review status must distinguish a confirmed absent submission from an unavailable native
  journal lookup. Native `submission.state="unavailable"` keeps the existing managed model
  blocked during contention/query errors, including after restart. Reuse the exact command
  ID and outcome after an ambiguous send; never infer collector outcomes from integrity.
  Independent checks fed real native outputs into the already-compiled RealityLog assembly:
  47 assertions passed, and the original producer output reproduced the defect.
- The Nov 12 cached build changed both RealityLog and RealityLog.UI. Normalized comparison
  found the other 101 of 103 linked assemblies and every Burst target unchanged. Private,
  nonserializable runtime snapshot layout exceptions require a reachability audit proving
  they are absent from serialized roots; do not weaken the general compatibility check.
  Installation verified the matched metadata/GUID and preserved config/recording directories.
  Moving capture, actual sealing, UI use and platform QC remain separate gates.

Evidence and exact commands: `/home/sarthak/p3/pod5-seal-fix-20261112/` (`tracking-audit`,
`host-tests.log`, `refute`, `build/csharp-chain`, `installation`). The candidate is app 1.5.0,
build 67, APK SHA-256 `0e57c2379711f3bad5b75f5f4fc2c40dde92c654a3aabf4a2e762bf196c922b3`.

### Residual acquisition gap and whole-status assembly (2026-11-12)

- The later moving episode `20261112_182250` completed its 41.378 s native window, but
  sealing refused one head gap of 105,263,605 ns against the unchanged 100,000,000 ns
  limit. Both controller gaps coincide. Camera queues peaked at 4/60 and 3/60 and all
  four USB streams completed. Sealing ran for about 6.15 s; this was a data rejection,
  not an absent worker or another camera overflow.
- Read delivered timestamp domains from the converter: head/controllers use host mono
  time, body uses OVR source time plus the shared head-clock offset. The body host poll
  also paused 105.274 ms, while its delivered maximum was 99.548 ms. A separate IMU
  thread recorded 16 fresh head samples inside the exact pose gap. That narrows the
  interruption to Unity acquisition but does not identify its method or scheduling state.
  No trace was captured for this episode; an earlier trace is not its causal evidence.
- Moving JSON parsing off-thread leaves whole-document construction on-thread unless
  `RefreshStatus` is also changed. A later live warning measured 22.07 ms constructing
  about 394 KB on Unity. This warning is outside the failed interval. The repair captures
  small main-owned fields and immutable JSON references, then constructs and parses on
  one bounded worker with only the newest pending request retained.
- Publish raw JSON and its tree as one value, and native JSON/summary as one observation.
  Advance lifecycle generation only on invalidation/disposal; advancing it on every offer
  can starve publication under load. Invalidate in-flight old-rig work on close/reopen and
  clear stale readiness on malformed input. Worker callbacks must not read Unity, JNI or
  mutable coordinator collections. Start admission still uses the existing live checks.
- `tools/sx-rig-stub/test_binding.sh` passed 676 assertions. Artifact-only synchronous-build,
  stale-generation and publication-starvation mutations each failed the same harness.
  `status-audit/check-repair.py` uses the exact old/new method bodies: the labeled 393,958
  character fixture produced identical bytes and caller allocation fell 1,594,248 to
  4,696 bytes/update. These are host fixture results, not Quest latency or GC proof.
- Optional `RigTrace` scopes use Android ATrace only while system tracing is enabled;
  unavailable symbols become a no-op. Preserve balanced begin/end on the same thread and
  verify actual device markers before relying on them. Do not add per-frame file logging.
- This continuation compiled RealityLog, RealityLog.UI and RealityLog.Network through
  the cached stock chain. The other 100/103 linked assemblies and all Burst-bearing
  assemblies were unchanged. A reused GUID action may still point at the previous run's
  staged inputs: rebase those paths to the newly hash-verified baseline copies before
  running the normal generator. Do not change the scene/settings/serialization contract.

Evidence: `/home/sarthak/p3/pod5-seal-fix-20261112/status-audit/`, `tracking-audit/latest-182250-report.md`
and `status-repair/`. A built repair still requires installation, startup/trace verification,
a genuine moving recording, actual unchanged sealing and canonical QC. Never describe a
host test or a short USB-aborted run as sustained capture acceptance.

### Body-rate admission, body gaps and the libsx_rig ABI drift (2026-11-15, sx#4210)

- Body-tracking gaps >100 ms are the top seal refusal (sx#4210: 489 episodes; 38-41% on
  v67 test builds). One fresh body sample passes a 100 ms age check even when the Meta
  tracker advances only every 160-320 ms (OQC#22), so `HeadTrackingMonitor.StartRefusal`
  also refuses below `StartMinBodyHz` (30) over `BodyRateWindow.WindowSeconds` (1.5 s of
  OVR source time) and while that window is not yet full ("still being measured").
- `OVRPlugin.BodyTrackingCalibrationState`: 1 Valid, 2 Calibrating, 3 Invalid (SDK 81
  `OVRPlugin.cs`). Calibrating alone does not refuse; every change is logged as
  `HeadTrackingMonitor: body calibration_status A -> B after N s`.
- `BodyTrackingLogger` counts intervals >100 ms between *written* rows into the body
  stream's manifest stats (`gaps_over_100ms`, `max_gap_ms`); `/api/status` tracking has
  `bodyUpdateHz`, `bodyCalibrationStatus`, `bodyRecordingGaps`, `bodyLongestGapMs`,
  `bodyTrackerRestarts`. Nothing in sx parses those (seal checks only that
  `stream_manifest.json` exists; the laptop pod reads `/tracking/startRefusal` by pointer).
- Idle recovery: with the rate refusing for 15 s and no recording, the logger runs
  `StopBodyTracking` + `StartBodyTracking2` at most once a minute. Not yet observed on a
  device to clear a degraded tracker. A frozen *head* pose still has no automatic recovery:
  quitting the app needs a relauncher, which a headset-only pod lacks.
- Do not add `tracked` to `DevicePresence`'s controllers: libsx_rig's `ControllerReport`
  is `deny_unknown_fields`, so the whole device report (battery, power_ok, clock) would be
  refused. The Pod card's "controller not tracked" warning reads the app's own
  `headset.controllers[].tracked` (position AND orientation), covered in PodScreenHarness.
- The vendored `libsx_rig.so` (sx main 9c0b8959dd) does not export `sx_rig_request_stop`;
  check with `nm -D --defined-only Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so`.
  `RequestStopEpisode` now warns once and skips it, so the stop alone ends capture.
- Host harness: `SX_RIG_INCLUDE=~/p3/sentient/sx/packages/sx-pod-runtime/include bash
  tools/sx-rig-stub/test_binding.sh` -> 694 `PASS` lines, exit 0 (676 before this change);
  tracking readiness 60/60. Mutations (threshold 5 Hz; rate clause removed) fail 3 and 5 checks.
