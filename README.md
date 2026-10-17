# OpenQuestCapture

[![](https://dcbadge.limes.pink/api/server/y5v3BTXAUd)](https://discord.gg/y5v3BTXAUd)

**Capture and store real-world sensor data on Meta Quest 3**


![Demo](docs/demo.gif)

**[Watch the full pipeline in action](https://www.youtube.com/watch?v=brcnfMmwRq8)** - See how data is captured on Quest and reconstructed into a 3D scene. *(Thank you to EggMan28 for the demo!)*

---

## 🙏 Acknowledgement

First I'd like to acknowledge the work of the author of QuestRealityCapture. This project largely is built on top of the Quest sensor data capture platform / library that they built. **[QuestRealityCapture](https://github.com/t-34400/QuestRealityCapture)** by **[t-34400](https://github.com/t-34400)**.

Huge thanks to the original author for their excellent work in making Quest sensor data accessible!

---

## 📖 Overview

`OpenQuestCapture` is a Unity-based data logging tool for Meta Quest 3 focused on long-duration robotics data collection.  
The default capture pipeline now records:

* both compressed passthrough camera videos (`.mp4`, left and right camera streams)
* synchronized pose logs (`hmd_poses.csv`, controller poses)
* synchronized IMU logs (`imu.csv`)
* camera characteristics JSON (one per stream)

Depth capture and dual raw-YUV capture are disabled by default to reduce storage pressure for multi-hour recording shifts.

---

---

## ✅ Features

* Records HMD/controller poses and IMU data (session-based)
* Captures both compressed passthrough camera streams, to `left_camera.mp4` and `right_camera.mp4`
* Logs Camera2 characteristics for each camera stream
* Uses timestamped session directories for long-run collection
* Keeps recording menu export/delete flows for storage management


### Quick Start Guide

1. **Install the app**: Download from [SideQuest](https://sidequestvr.com/app/45514) or sideload the APK file from the Releases section of this repository. [This video](https://www.youtube.com/watch?v=bsC805t63-E) has a good guide on how to set up sideloading.
2. **!!! IMPORTANT !!! Enable permissions**: **When you first launch the app, make sure to check "Enable Headset Cameras" when the permissions are asked for.**
3. **Start recording**: Launch the app and press the Menu button on the left controller to start a capture session.
4. **Stop recording**: To stop, press the left controller's Menu button again.
5. **Move the data from your Quest to your computer**: The data is stored on the Quest's internal storage. You can move it to your computer using a USB cable by connecting the Quest to your computer and using Windows File Explorer. The data is stored in the `/Quest 3/Internal Shared Storage/data/com.samusynth.OpenQuestCapture/files` directory.
Or, you can use press the Y button on the left controller to toggle the Recording Menu. Select "Export Data" to export the data to a zip file in the Quest 3 Download folder which can be uploaded to Google Drive or other cloud storage services.
6. **Post-process on laptop**: Combine `left_camera.mp4` / `right_camera.mp4` + pose/IMU CSV files into your downstream format (for example, MCAP/Foxglove pipelines).

### 📸 How to take a good capture

To ensure stable long-form robotics capture:

1.  **Lighting**: Ensure the environment is well-lit and consistent. Avoid extreme shadows or blinding lights.
2.  **Movement**: Move steadily and avoid rapid head snaps that cause motion blur.
3.  **Session length**: For shift workflows, run longer sessions and export/delete regularly from the recording menu.

---

## 🧾 Data Structure

Each time you start recording, a new folder is created under:

```
/sdcard/Android/data/com.samusynth.OpenQuestCapture/files
```

Example structure:

```
/sdcard/Android/data/com.samusynth.OpenQuestCapture/files
└── YYYYMMDD_hhmmss/
    ├── left_camera.mp4
    ├── left_camera_characteristics.json
    ├── left_camera_metadata.json
    ├── left_camera_timestamps.csv
    │
    ├── right_camera.mp4
    ├── right_camera_characteristics.json
    ├── right_camera_metadata.json
    ├── right_camera_timestamps.csv
    │
    ├── tracking_origin.json
    ├── hmd_poses.csv
    ├── left_controller_poses.csv
    ├── right_controller_poses.csv
    ├── imu.csv
    ├── stream_manifest.json
    └── rig_stop_receipt.json      (only when the wrist rig recorded; see below)
```

---

## 📄 Data Format Details

### Pose CSV

* Files: `hmd_poses.csv`, `left_controller_poses.csv`, `right_controller_poses.csv`
* Format:

  ```
  unix_time,ovr_timestamp,mono_time_ns,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w
  ```

### Camera Characteristics (JSON)

* Obtained via Android Camera2 API
* Includes pose, intrinsics (fx, fy, cx, cy), sensor info, etc.
* Since capture contract 1.4.3, `outputSizes` lists the camera's SurfaceTexture output sizes (Camera2 `SCALER_STREAM_CONFIGURATION_MAP`). The intrinsics are calibrated on the sensor `pixelArraySize` (1280×1280 on the Quest 3S); the recorder only ever asks Camera2 for a size from this list.

### Camera Video (MP4)

* Files: `left_camera.mp4` (left camera), `right_camera.mp4` (right camera)
* Codec: H.264 inside a **fragmented** MP4 (since app 1.5.0): an empty-sample `moov`, then one `moof`+`mdat` per one-second GOP, each fsynced as it is written. A process killed mid-recording (`am force-stop`, crash) leaves a file that plays up to the last complete GOP, and the `*_timestamps.csv` sidecar is flushed and fsynced with every fragment, so it covers the same frames (minus rows still waiting on their capture result) instead of being 0 bytes. A clean stop patches the real duration into the `moov`. `ffprobe` reports `nb_frames` as N/A for a fragmented file; count frames with `-count_frames` or use `avg_frame_rate`.
* Geometry (capture contract 1.4.3, written by OpenQuestCapture 1.4.5 — app version and contract version are different numbers: app 1.4.5 ↔ contract 1.4.3, app 1.4.4 ↔ contract 1.4.2, app 1.4.3 ↔ contract 1.4.1): the recorder asks Camera2 for the sensor's native listed stream — the `pixelArraySize`, 1280×1280 on the Quest 3S — and encodes it at 720×720, an isotropic resample of the whole array (`fx, fy, cx, cy` of the MP4 are 720/1280 of the sensor calibration; no crop, no stretch). Before contract 1.4.3 the recorder asked for 720×720, a size the passthrough camera does not list; the camera service silently rounded that to 720×576, the virtual camera centre-cropped its 1280×1280 source to 5:4 to fill it, and the recorder stretched the result square — so those MP4s show a 5:4 slice of the field of view stretched to a square, and nothing in the sidecar recorded it. OpenQuestCapture 1.4.5 (capture contract 1.4.3) refuses to start (the start request fails with the reason; nothing is written) when the requested source size is not in the camera's listed `outputSizes` or the encoder output does not keep its aspect, and its metadata states the four geometry keys below so a consumer can check what was delivered instead of assuming it.
* Camera videos are intentionally video-only. The app requests Camera2 `CONTROL_AE_TARGET_FPS_RANGE` `[60,60]`; the Quest 3S HAL answers with 50 fps on one 20 ms lattice shared by both cameras, whose `SENSOR_TIMESTAMP`s are identical (at `[30,30]` it decimates that lattice independently per camera, so paired frames landed 20 ms apart about half the time). A MediaCodec/EGL path selects the first fresh exposure in each absolute 33.33 ms bin of the sensor clock, so both eyes deliver the same instants — 3 of every 5 lattice slots, gaps of 20/40/40 ms, mean 30 Hz — never manufactures a duplicate, and writes an exact n/30 presentation timeline into the MP4. A source already at ≤35 Hz passes through. Frame stamps remain `SENSOR_TIMESTAMP` (start of exposure).
* `left_camera_timestamps.csv` and `right_camera_timestamps.csv` contain one row per encoded frame. `sensor_timestamp_ns` is the Camera2 exposure-start `SENSOR_TIMESTAMP` (the SurfaceTexture timestamp of the frame that was encoded, so it is never missing). `exposure_time_ns` and `capture_frame_number` are joined from the frame's `TotalCaptureResult`; since capture contract 1.4.1 the recorder writes `-1` when the HAL omitted the exposure duration (Quest 3S does so sporadically) or never delivered the result, and counts both in the metadata `capture_report`. Bookkeeping never truncates an eye — only an encoder/EGL failure ends a recording, and that is reported verbatim as `capture_error`.
* Intended for long-duration collection where storage efficiency is critical

Each stream writes its own sidecar metadata — `left_camera_metadata.json` for the left stream,
`right_camera_metadata.json` for the right — holding that stream's start/stop stamps and, since
capture contract 1.4.3, its stream geometry: `requested_stream_size` (what Camera2 was asked
for), `source_stream_size` (the SurfaceTexture default buffer size — the same value by
construction, written so the sidecar states it), `output_size` (the encoder, i.e. the MP4) and
`texture_transform` (the first `SurfaceTexture.getTransformMatrix()` the recorder saw, 16
floats column-major, or `null` if no frame arrived) (example values):

```json
{
  "capture_contract_version": "1.4.3",
  "recording_start_unix_ms": 1753267694123,
  "recording_stop_unix_ms": 1753267754456,
  "recording_start_mono_ns": 84213000000,
  "recording_stop_mono_ns": 144546000000,
  "configured_fps": 30,
  "gop_frames": 30,
  "requested_stream_size": {"width": 1280, "height": 1280},
  "source_stream_size": {"width": 1280, "height": 1280},
  "output_size": {"width": 720, "height": 720},
  "texture_transform": [1.0, 0.0, 0.0, 0.0, 0.0, -1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 1.0, 0.0, 1.0],
  "video_file": "right_camera.mp4",
  "frame_timestamps_file": "right_camera_timestamps.csv",
  "frame_timestamps_schema": "openquest.camera_frame_timestamps/v2",
  "frame_timestamp_semantics": "sensor_exposure_start",
  "sensor_timestamp_source": "UNKNOWN",
  "source_frame_count": 3017,
  "source_dropped_frame_count": 0,
  "selected_frame_count": 1810,
  "capture_report": {"requested_fps_range": [60, 60], "observed_source_fps": 50.0000, "selection_mode": "absolute_grid", "selection_grid_ns": 33333333, "exposure_missing_frame_count": 1, "capture_result_missing_frame_count": 0, "source_sequence_error_count": 0, "timestamp_regression_count": 0, "gop_violation_count": 0, "selected_mean_fps": 30.0000, "selected_max_gap_ns": 40000000},
  "capture_error": null,
  "audio_enabled": false,
  "audio_bitrate": 128000,
  "audio_sampling_rate": 44100
}
```

`recording_start_mono_ns` is read directly from Android
`clock_gettime(CLOCK_MONOTONIC)`, the same counter used by the CSVs' `mono_time_ns` column
and the `POST /api/timesync/ping` endpoint. The two videos and every logged sample therefore
map onto a host clock through the episode's measured affine clock fit.

For frame-accurate alignment, consumers use each row's `sensor_timestamp_ns`, not the
recording-start estimate or MP4 packet delivery time. Horizon OS reports the passthrough
cameras' Android timestamp source as `UNKNOWN`; the converter preserves that provenance and
maps the Quest sensor/OVR clock to `mono_time_ns` only after the episode's HMD clock pairs prove
a residual of at most 2 ms.

### Wrist rig (libsx_rig)

When the headset is the whole pod, its wrist cameras (UVC `32e4:9230`) and gripper encoders
(ESP32-C6, `303a:*`) are plugged into it and recorded by `libsx_rig.so` (Rust, sx
`experience/data-factory/quest-rig`, C ABI `include/sx_rig.h`; the one C# binding is
`Assets/RealityLog/Scripts/Runtime/Rig/SxRig.cs`). The rig is **expected** when
`rig_config.json` (roles and calibration bundle from Factory) is in the app's files directory or
any rig USB device is on the bus; with neither, nothing below happens and recording is as before.

* `POST /api/start-recording` runs the rig's device check and arms it (`sx_rig_start`, up to
  12 s) on the HTTP worker thread, before OQC's streams start, and refuses with
  `503 {"error":"rig_not_ready","reason":"rig_library_missing|rig_open_failed|rig_usb_permission|rig_check_failed|rig_start_failed","message":...,"rig":<library error>}`.
  The rig starts with `CLOCK_MONOTONIC` and UTC anchors read back to back; the session directory
  name is the rig's `episode_id`. While the rig is expected, the other start paths (cloud relay,
  calibration) are refused with `rig_not_armed`.
* `POST /api/stop-recording` returns the rig's stop receipt as `"rig"` (and writes it to
  `rig_stop_receipt.json`); `GET /api/status` has a `"rig"` object.
* **Local mode (no Factory).** With no `factory_url` in `pod_config.json` the headset records on
  its own: put the YUBI rig in `rig_config.json` (its roles, identities and calibration, in the
  shape the rig defines; passed through as the open config's `rig`), and `operator_id` in
  `pod_config.json` if start bodies will not carry one. Nothing asks for enrollment or sign-in.
  `POST /api/start-recording` and `/api/stop-recording` record episode after episode; each stop
  seals that episode on the headset (`rig_staging/<session>/episode.mcap` + `seal.json`).
  `/api/status` → `rig.seals` holds each episode's verdict (`sealed` | `refused` | `pending`, with
  `detail`), `storageFreeBytes` the free space; a start without room is refused `507`
  (`storage_full`, or `rig_not_ready`/`rig_storage_low` below the rig's floor). `GET
  /api/recordings` lists each session's `seal` and `episodeMcap`, and
  `GET /api/recordings/<session>?artifact=mcap` downloads it (HTTP ranges supported). The operator
  hears every outcome: start rises, stop falls, sealed is three high blips, refused a low buzz.
* **Start order.** `POST /api/start-recording` checks the rig (`sx_rig_check`), starts OQC's
  session, then calls `sx_rig_start_armed`: the rig arms every stream and anchors the episode
  itself once all deliver (about 2–3 s), so the episode's window lies inside OQC's recording, as
  the seal requires (it refuses an episode whose HMD poses do not cover it). The start response's
  `rig` holds that anchor (`episode_id`, `monotonic_anchor_ns`, `utc_anchor_ns`,
  `arm_utc_anchor`, `arming_ms`), also written to the session as `rig_start.json`. A refused
  arming stops OQC's session again (`rig_refusal.json` beside its files) and answers the
  refusal with the session as `file`. A storage refusal is `507` with the rig's numbers under
  `rig.refusal` (`free_bytes`, `required_bytes`, `episode_bytes`, `backlog_bytes`,
  `backlog_episodes`, `max_episode_seconds`); every refusal plays the refused cue.
* **Controls.** The operator wears the headset on the forehead and never sees the display. The
  **left controller's menu button** (three lines) starts a recording and, pressed again, stops it
  (the same path as `POST /api/start-recording` / `/api/stop-recording`, with the operator from
  `pod_config.json`); presses while a start or stop is running are ignored. Outcomes are sounds:
  start rises, stop falls, a refusal buzzes, a sealed episode blips three times. **Home** is the
  right controller's Oculus button.
* **USB permission on Horizon OS:** a UVC camera's USB permission is silently denied unless the
  app holds `android.permission.CAMERA` and `horizonos.permission.USB_CAMERA`; both are asked at
  first launch with the passthrough permissions (someone wearing the headset accepts them once).
* Enrollment: `POST /api/enroll {"join_code": "sxj_...", "base_url": "https://app.preview.sentientx.io"}`
  (`base_url` optional; production otherwise, saved to `pod_config.json`) commissions the headset
  as a Factory pod of kind `headset`. The machine credential is kept in app-private storage; once
  it exists the rig is expected, resolves its registered rig from the Factory, and seals, uploads
  and retires episodes on its own. `pod_config.json` (files directory) may also set `pod_name`,
  `rig_type_id`, `upload_limit_mib_s`, `seal`, `min_free_bytes`, `tailscale_ip`.
* **Provisioning a YUBI headset for local mode (until it is enrolled).** The rig is the
  Factory's resolved-rig shape (`rig_id`, `bundle_id`, `bundle` with components, calibration
  files and URDF digest), as the Factory serves it once enrolled; for local mode it is pushed:

  ```bash
  files=/sdcard/Android/data/com.samusynth.OpenQuestCapture/files
  serial=$(adb shell getprop ro.serialno | tr -d '\r')
  printf '{"headset_serial":"%s","operator_id":"<operator>","rig_type_id":"rig_yubi"}\n' "$serial" > pod_config.json
  adb push pod_config.json "$files/pod_config.json"
  adb push <calibration dir>/rig_config.yubi.json "$files/rig_config.json"   # e.g. yubi-06-quest
  adb push quest_builds.json "$files/quest_builds.json"   # accepted headset builds, shape defined by the rig
  ```

  Once enrolled the rig resolves its rig and the accepted builds from the Factory, and these
  files are no longer read for them.
* **Pod setup (enrolling a headset)** is in the in-headset recordings menu. *Scan QR code* reads
  the console's *Enroll a pod → Headset* code (`{"site", "join_code"}`) through the passthrough
  camera and enrolls at once; *Site* and *Join code* open the Quest keyboard as a fallback, then
  *Enroll*. Each step answers with a status line and a sound (blips when scanned, a chime when
  enrolled, a buzz with the reason on any failure). Enrolling turns Local only off for the
  attempt and leaves it off on success (restored on failure). The serial stays
  `pod_config.json`'s.
* **The HTTP API in local mode** is the open LAN API it has always been (no token): Local only
  never reaches the Factory, and the headset sits on the operator's network. A Factory-enrolled
  headset will require the token the rig derives from its enrollment credential (follows the
  rig's ABI).
* **Provisioning (once per headset, before enrolling):** apps cannot read `ro.serialno` on the
  Quest 3S, so write the serial into `pod_config.json` over adb (the shell can read it). This
  overwrites the file; if one already exists, add the key by hand instead:

  ```bash
  serial=$(adb shell getprop ro.serialno | tr -d '\r')
  printf '{"headset_serial":"%s","rig_type_id":"rig_yubi"}\n' "$serial" > pod_config.json
  adb push pod_config.json /sdcard/Android/data/com.samusynth.OpenQuestCapture/files/pod_config.json
  ```

  The app passes this file to the rig as `pod_config_path`. `rig_type_id` (the registered rig
  type the headset commissions as) has no default in code; `POST /api/enroll` refuses with
  `409 enroll_failed` (reason `pod_config_incomplete`) without it. The site (`factory_url`,
  `auth_url`) is written by enrollment: its `base_url`, else production.
* **Heartbeat readiness:** every 15 s the app reports `battery_percent`, `charging` (plugged in)
  and `network_time_synchronized` to the rig; the Factory heartbeat trusts a report for 60 s.
  The time flag is true only when `SystemClock.currentNetworkTimeClock()` answers (the platform
  holds a network time fix; Quest 3S is SDK 34). A stalled app stops reporting and the pod reads
  not ready. `rig.deviceReport` in `/api/status` is the last report; `rig.nativeState` is the
  rig's state, `failed` after an internal failure (every rig call then fails until it is reopened).
* **Collector sign-in:** on an enrolled headset a start needs a signed-in collector, else
  `409 {"error":"rig_not_ready","reason":"collector_signed_out"}`. `POST /api/sign-in` answers
  `{"status":"pending","user_code","verification_uri_complete","expires_in"}`; the caller (pod
  operator, companion app) shows the code, since the wearer cannot see the display, and the
  collector approves on their own phone. The headset chimes when the sign-in lands, and
  `rig.floor.operator.signed_in` turns true. `POST /api/sign-out` signs out (409 while recording).
* **Collector commands:** `POST /api/command` passes `{"command":"review_success"|"review_failure",
  "episode_id","answers":[...],"failure_cause"?}` or `{"command":"end_collection"|"finalize"|
  "close_failed_collection"}` to the rig and answers `{"answer","floor"}`; a `command_id` (UUID)
  makes a retried command the same command, and one is generated when absent. Errors:
  400 `invalid_body` | `unknown_command` | `command_refused` (argument), 409 `not_enrolled` |
  `command_refused` (reason `state`, e.g. a review before the Factory observed the attempt:
  retry), 503 `command_refused`.
* While the rig is expected, the start body must carry `"operator_id"` (else `400
  operator_id_required`) and may carry `"task_assignment_id"` and `"mode"`
  (`"directed"`|`"undirected"`), which open the collection on the Factory; the episode also records `quest_apk_build_id` (`sha256:` of the
  installed APK, as the laptop pod computes it) and `quest_session_dir`, which the seal reads.
  The rig stops after every OQC file of the session is final, so the seal never reads a partial one.
* `GET /api/status` → `rig` carries `enrolled`, `podId`, `registeredRig`, `seals`, `uplink`,
  `floor` (operator sign-in, open collection, pending commands),
  `headsetSerial`, `tailscaleIp` and `apkBuildId`.
* **Embodiment meshes for the on-headset seal** (11 `yubi_description` files, 18.9 MB) are
  generated, not committed: before every APK build run
  `SX_QUEST_RIG=<sx>/experience/data-factory/quest-rig scripts/build_embodiment_assets.sh`
  (add `OFFLINE=1` to build offline). It writes `Assets/StreamingAssets/sx-embodiments/` (each
  file by logical path plus `manifest.json`, gitignored); `QuestBuild.Build` refuses without it.
  Unity ships StreamingAssets uncompressed in the APK's `assets/`; on launch the app copies them
  to `<filesDir>/sx-embodiments/` when its installed manifest differs (each file checked against
  its SHA-256, fsynced, renamed; manifest last) and passes that directory to the rig as
  `embodiment_assets_dir`. `/api/status` → `rig.embodiment_assets` is the rig's verdict
  (`ready`, `configured`, `assets`, `missing`), `rig.embodimentInstall` the app's copy.
* USB permission: plugging a rig device in the first time asks, in the headset, which app opens
  it; tick "always open" for OpenQuestCapture once and later replugs are silent.
* `Assets/Plugins/Android/libs/arm64-v8a/libsx_rig.so` is built from sx
  `experience/data-factory/quest-rig` (`cargo build --release --target aarch64-linux-android`,
  see its `scripts/build-android.sh`) and copied here; the commit that adds it names its source
  commit and SHA-256. `tools/sx-rig-stub` builds a stub for host tests only; no APK ships it.

`tracking_origin.json` records the configured and runtime tracking-origin modes, whether
recentering was allowed, the runtime floor-reference semantics, the recenter counter at both
session boundaries, and every observed recenter event on the shared monotonic clock.

---

## 🚀 Installation & Usage

### For End Users

**Option 1: Install via SideQuest**

Download and install directly from [SideQuest](https://sidequestvr.com/app/45514).

**Option 2: Manual Sideloading**

Side-loading is required to install this app on the Quest. [This video](https://www.youtube.com/watch?v=bsC805t63-E) has a good guide on how to set up sideloading. The most up to date APK can be found in the [Releases](https://github.com/samuelm2/OpenQuestCapture/releases) section of this repository.


## 🎮 Usage

### Recording & Management

1. **Start/Stop Recording**: 
   - Launch the app.
   - Press the **Menu button** on the left controller to dismiss the instruction panel and start logging.
   - To stop, simply close the app or pause the session.

2. **Manage Recordings**:
   - Press the **Y button** on the left controller to toggle the **Recording Menu**.
   - This menu allows you to:
     - **View** a list of all recorded sessions.
     - **Export** sessions to a zip file (saved directly to the Quest Downloads folder: `/Download/Export/`).
     - **Delete** unwanted sessions to free up space.


---

## ☁️ Cloud Processing (Recommended)

For the easiest workflow, you can upload your exported `.zip` files directly to the vid2scene cloud processing service:

**[vid2scene.com/upload/quest](https://vid2scene.com/upload/quest)**

This service will automatically process your data and generate a 3D reconstruction.

---

## 💻 Local Processing & Reconstruction

If you prefer to process data locally, this project includes a submodule **[quest-3d-reconstruction](https://github.com/samuelm2/quest-3d-reconstruction)** with powerful Python scripts.

### Setup

Ensure you have the submodule initialized:

```bash
git submodule update --init --recursive
```

### End-to-End Pipeline

The `e2e_quest_to_colmap.py` script provides a one-step solution to convert your Quest data into a COLMAP format.

**Usage Example:**

```bash
python quest-3d-reconstruction/scripts/e2e_quest_to_colmap.py \
  --project_dir /path/to/extracted/session/folder \
  --output_dir /path/to/output/colmap/project \ 
  --use_colored_pointcloud
```

Once in colmap format, the reconstruction can be passed into various Gaussian Splatting tools to generate a Gaussian Splatting scene.

**What this script does:**
1. **Converts YUV images** to RGB.
2. **Reconstructs the 3D scene** (point cloud).
3. **Exports** everything (images, cameras, points) to a COLMAP sparse model.

---

## 🎨 Tone Mapping for Indoor Scenes

If you're capturing indoor scenes with windows, you may notice that bright window light can blow out the interior lighting, resulting in overexposed highlights and underexposed shadows. The reconstruction pipeline includes **tone mapping** (CLAHE + gamma correction) to fix this issue during YUV→RGB conversion.

This is **especially effective for indoor environments** where natural light from windows creates high dynamic range conditions that exceed the camera's capture capability.

### How to Enable

Edit `quest-3d-reconstruction/config/pipeline_config.yml` before running the reconstruction:

```yaml
yuv_to_rgb:
  tone_mapping: true              # Enable tone mapping
  tone_mapping_method: "clahe+gamma"  # Options: "clahe", "gamma", "clahe+gamma"
  clahe_clip_limit: 2.0           # Contrast enhancement (1.0-4.0)
  clahe_tile_grid_size: 8         # Local adaptation grid size (4-16)
  gamma_correction: 1.2           # Brightness boost (>1 brightens)
```

Then run the pipeline as normal. The tone mapping will be applied automatically when converting YUV images to RGB.

For more details and advanced options, see the full documentation in the [quest-3d-reconstruction README](https://github.com/samuelm2/quest-3d-reconstruction#-tone-mapping-for-high-dynamic-range-scenes).

---

## 🛠 Environment

* Unity **6000.2.9f1**
* Meta OpenXR SDK
* Meta MRUK (Mixed Reality Utility Kit)
* Device: Meta Quest 3 or 3s only


---

## 📝 License

This project is licensed under the **[MIT License](LICENSE)**.

This project uses Meta’s OpenXR SDK — please ensure compliance with its license when redistributing.

---

## 📌 Call for Contributions

This project is in its early stages and is still in active development. If you have any suggestions, bug reports, or feature requests, please open an issue or submit a pull request.

Join our Discord community to discuss ideas, get help, and share your captures with other users:

[![](https://dcbadge.limes.pink/api/server/y5v3BTXAUd)](https://discord.gg/y5v3BTXAUd)

One area improvment is the export process. Currently it can take several minutes to export a session. It would be great to have a faster way to do this.
