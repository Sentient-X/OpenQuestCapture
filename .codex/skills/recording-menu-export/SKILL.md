---
name: recording-menu-export
description: Implement and maintain the Quest recording menu, recording list, export/delete flows, and world-space VR menu behavior in OpenQuestCapture. Use when modifying UI scripts under Assets/RealityLog/Scripts/Runtime/UI, file operations under Runtime/FileOperations, or scene wiring for menu and recording feedback.
---

# Recording Menu Export

Use this skill to safely evolve recording menu UX and recording file operations on Quest without breaking scene hookups or async export behavior.

## Read These Files First

- `Assets/RealityLog/Scripts/Runtime/UI/RecordingMenuController.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/RecordingListUI.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/RecordingListManager.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/RecordingListItemUI.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/RecordingIndicator.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/RecordingSavedNotification.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/WorldSpaceMenuPositioner.cs`
- `Assets/RealityLog/Scripts/Runtime/FileOperations/RecordingOperations.cs`
- `Assets/RealityLog/Scripts/Runtime/UI/README_RECORDING_MENU_SETUP.md`
- `Assets/RealityLog/Scenes/RealityLogScene.unity`

## Preserve Behavioral Invariants

- Keep Y-button menu toggle behavior in `RecordingMenuController`.
- Keep operation lifecycle events wired:
  - `OnOperationProgress` for progress updates.
  - `OnOperationComplete` for final status and refresh.
- Keep export/compress work asynchronous and restore `Application.runInBackground` after completion.
- Keep media scan calls after move/export so files appear in Quest file browsers.
- Keep list refresh behavior after delete operations.
- Keep `RecordingManager.onRecordingSaved -> RecordingSavedNotification.OnRecordingSaved` hookup.

## Execute Change Workflow

1. Identify if the change is UI-only, file-ops-only, or both.
2. Modify UI scripts and keep event subscribe/unsubscribe symmetry in `OnEnable`/`OnDisable`.
3. Modify `RecordingOperations` with care for threading, progress callbacks, and permissions.
4. Update scene/prefab serialized references only when new fields are introduced.
5. Re-check world-space menu positioning behavior when changing visibility/placement logic.

## Verification Checklist

- Run targeted static checks:
  - `rg -n "OnEnable|OnDisable|OnOperationProgress|OnOperationComplete|RefreshList|ToggleMenu" Assets/RealityLog/Scripts/Runtime/UI`
  - `rg -n "runInBackground|Permission|Export|Compress|ScanFile|ScanDirectory" Assets/RealityLog/Scripts/Runtime/FileOperations/RecordingOperations.cs`
- Validate scene hooks:
  - `rg -n "RecordingListUI|RecordingMenuController|RecordingSavedNotification|RecordingOperations|RecordingListManager" Assets/RealityLog/Scenes/RealityLogScene.unity`
- If Unity CLI is available, run a batch compile pass and scan for compile/runtime errors.
- If Unity CLI is unavailable, explicitly report that runtime compilation was not executed.

## Definition of Done

- Keep menu toggle, recording list rendering, and feedback labels functional.
- Keep export/delete operations responsive and non-blocking.
- Keep scene bindings valid for all changed serialized fields.
- Keep permission and media-scan behavior intact on Android.
