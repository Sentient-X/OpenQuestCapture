# Table preview candidate — not qualified for deployment

The pod can request `GET /api/preview.jpg` during recording. The response is a
512 px JPEG from the existing left camera texture with `X-Capture-Time-Ns` in
Quest CLOCK_MONOTONIC nanoseconds. The server fits OVR camera time to that clock
using bracketed samples and refuses an inconsistent mapping. The pod applies its
admitted Quest-to-host arm offset and discards stale or future frames.

The QuestCameraLib source dependency is published in
[QuestCameraLib PR #2](https://github.com/Sentient-X/QuestCameraLib/pull/2).
The rebuilt AAR SHA-256 is
`1896348069035e1b41e3f99a9722397c4eb5dda16a10bc9b150a2977ae856c78`.
It uses a GLES 3 pixel-pack buffer, zero-timeout fence polling and a separate JPEG
worker. Requests are bounded to 2 Hz, with one in flight; no second Camera2
session is opened. GPU contention still requires measurement on hardware.

Local checks passed: Kotlin `:app:assembleRelease`; Unity 6000.3.6f1 Android-target
batch compilation. These checks do not establish camera orientation, frame timing
or performance on a real headset. This candidate has not been installed or deployed.

Before rollout, validate the candidate APK with the pod on cable/no-cable/occluded
scenes, move the cable during recording, interrupt preview/model connectivity,
and compare FPS, dropped frames, stereo timing and stop latency with preview
on/off. Verify retained warning evidence and episode SQL status after upload with
real auth. The pod repository's approved APK digest must remain unchanged until
that qualification succeeds.
