# Controller-to-Gripper Calibration

## Physical Setup

Quest 3 controllers are rigidly mounted to Genrobot gripper bodies using 3D-printed fixtures bonded with super glue. The mount is permanent per gripper — the transform does not change during operation.

Each gripper has its own fixture geometry, so:

- **Left gripper** has a unique controller offset (position + orientation)
- **Right gripper** has a different unique controller offset

## Coordinate Frames

```
Gripper TCP  <──  T_controller_to_gripper  ──  Quest Controller Pose
 (target)          (what we calibrate)          (what PoseLogger records)
```

- **Quest Controller Pose**: Reported by `OVRPlugin.GetNodePoseStateImmediate()` for `HandLeft` / `HandRight` nodes. Logged by `PoseLogger` as `(pos_x, pos_y, pos_z, rot_x, rot_y, rot_z, rot_w)` in tracking-space coordinates.
- **T_controller_to_gripper**: A rigid 6-DOF transform (translation + quaternion rotation) from the controller's body frame to the gripper's tool center point (TCP).
- **Gripper TCP**: The effective end-effector position in the same tracking-space coordinate system.

## Transform Convention

```
P_gripper = T_controller_to_gripper * P_controller
```

Where `T_controller_to_gripper` is stored as:

| Field | Unit | Description |
|-------|------|-------------|
| `translation.x/y/z` | meters | Offset from controller origin to gripper TCP in controller-local frame |
| `rotation.x/y/z/w` | quaternion | Rotation from controller frame to gripper frame (Unity convention: left-handed, Y-up) |

## Stored Transforms

Calibrated values are in [`controller_to_gripper_transforms.json`](./controller_to_gripper_transforms.json).

## How to Calibrate

> TODO: Document calibration procedure once established.
>
> Likely approaches:
> 1. **Known-point method**: Touch gripper TCP to a known reference point from multiple orientations, solve for the rigid offset.
> 2. **CAD measurement**: Measure offset directly from the 3D-printed fixture CAD model + controller body CAD.
> 3. **Optical tracking cross-reference**: Use an external tracker on the gripper TCP and compare against Quest controller poses.

## Important Notes

- These transforms are **per-physical-gripper**, not per-session. They only change if the fixture is remade or the controller is remounted.
- The Quest controller tracking origin is roughly at the center of the controller's IMU cluster — not at any visible surface point.
- All values use **Unity's coordinate system** (left-handed, Y-up) to match PoseLogger output.
