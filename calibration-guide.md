# Pose Calibration Recording Guide

## Purpose
Record a dedicated calibration episode to learn the rigid body transform
between VR controllers and gripper hardware. Run once per gripper assembly
(or whenever the 3D-printed fixture is changed/reglued).

## Prerequisites
- Grippers assembled with VR controllers via 3D-printed fixtures
- Quest 3S tracking active and controllers paired
- **Genrobot grippers powered on and MCAP recording started** — the solver
  reads IMU data (`angular_velocity` + `linear_acceleration`) from the
  gripper's `/robot0/sensor/imu` MCAP topic. No recording = no IMU = no
  calibration.
- Quest controller recording running (produces `{left,right}_controller_poses.csv`)

## Recording Protocol (~90 seconds total)

Start the Genrobot gripper recording first, then start the Quest recording.
The solver handles arbitrary clock offsets between the two devices via
cross-correlation (tested with 20+ second offsets).

### Phase 1: Stationary bias calibration (10s)
Place both grippers on a flat surface. Do not touch them.
This period is used to estimate gyroscope DC bias.

### Phase 2: Static orientation holds (30s)
Pick up one gripper assembly at a time. Hold it steady for ~3s
at each of 5+ distinct orientations:
- Upright (normal grip position)
- Tilted 45 degrees left
- Tilted 45 degrees right
- Tilted 45 degrees forward
- Tilted 45 degrees back
- Upside down (if feasible)

**Critical:** Hold genuinely still at each orientation. The accelerometer
must read only gravity (no hand tremor). Rest your elbow on a surface.

### Phase 3: Dynamic excitation (45s)
Wave each gripper assembly vigorously through ALL rotation axes:
- Roll (wrist rotation)
- Pitch (tilting forward/back)
- Yaw (turning left/right)
- Combined figure-8 patterns

**Critical:** Cover all 3 rotation axes. Single-axis motion gives a
degenerate calibration (the solver will warn "Poor conditioning").
Move fast -- higher angular velocity = better signal-to-noise.

### Phase 4: Final stationary period (5s)
Place both grippers back on the surface. Do not touch them.
Used as a validation check against Phase 1.

## After Recording

**No need to run `umico run` or `process-episode` first.** The calibrate-pose
command works directly on raw data and handles time alignment internally
(cross-correlation on angular velocity magnitudes).

1. Create the episode directory with `vr/` and `gripper/` subdirectories
   containing the raw recordings (symlinks to original recording dir work fine)
2. The `vr/` directory should contain `left_controller_poses.csv` and
   `right_controller_poses.csv` (from the Quest recording's output)
3. The `gripper/` directory should contain the raw MCAP files
   (e.g. `left_leader.mcap`, `right_follower.mcap`)
4. Run: `umico calibrate-pose data/episodes/<calibration_episode_id>`
5. Inspect: `cat data/registries/pose_calibration.json`
6. Check: `residual_deg < 3.0` and `correlation_score > 0.85`

## Quality Checks

| Metric | Good | Acceptable | Re-record |
|--------|------|------------|-----------|
| `residual_deg` | < 1.0 | < 3.0 | > 5.0 |
| `max_residual_deg` | < 5.0 | < 10.0 | > 15.0 |
| Conditioning warning | No | OK if residual low | Yes if residual high |

## Tips for Best Results
- Record in a well-lit room (Quest tracking accuracy depends on it)
- Avoid magnetic interference near the grippers (affects IMU)
- If you get a "Poor conditioning" warning, add more rotation diversity
  (the first recording had this but still achieved 0.25-0.38 degree error)
- Multiple calibration episodes can be averaged for higher precision
