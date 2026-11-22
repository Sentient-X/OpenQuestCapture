#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RealityLog.Rig;
using RealityLog.UI;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

/// <summary>
/// Renders the recordings menu with its Pod page fed fixed states, the way the headset draws it,
/// to PNGs under ~/p3/tmp/shots/podcard-&lt;state&gt;.png, and lists any row that overlaps the next
/// or any text taller than its cell in ~/p3/tmp/shots/podcard-check.txt. Run in batchmode (with
/// graphics, so not -nographics):
///   Unity -batchmode -quit -projectPath . -buildTarget Android -executeMethod PodCardSnapshot.Run
/// </summary>
public static class PodCardSnapshot
{
    private const string Scene = "Assets/RealityLog/Scenes/RealityLogScene.unity";
    private const int Pixels = 1100;

    public static void Run()
    {
        var code = 0;
        try
        {
            Render();
        }
        catch (Exception ex)
        {
            Debug.LogError("PodCardSnapshot failed: " + ex);
            code = 1;
        }
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    private static void Render()
    {
        var outDir = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? ".", "p3", "tmp", "shots");
        Directory.CreateDirectory(outDir);
        // The operator's words, as RigCoordinator loads them on the headset.
        OperatorNotices.Load(File.ReadAllText("Assets/RealityLog/Resources/sx_operator_notices.json"));
        EditorSceneManager.OpenScene(Scene);

        var ui = Object.FindFirstObjectByType<RecordingListUI>(FindObjectsInactive.Include)
            ?? throw new InvalidOperationException("no RecordingListUI in " + Scene);
        var canvas = ui.GetComponentInParent<Canvas>(true);
        canvas.transform.SetParent(null, false);
        canvas.transform.SetPositionAndRotation(new Vector3(0, 0, 10), Quaternion.identity);
        canvas.transform.localScale = Vector3.one * 0.001f;
        if (ui.TryGetComponent<CanvasGroup>(out var group)) group.alpha = 1;

        foreach (var other in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            other.enabled = false;
        }
        var camera = new GameObject("SnapshotCamera").AddComponent<Camera>();
        camera.transform.SetPositionAndRotation(new Vector3(0, 0, 9), Quaternion.identity);
        camera.orthographic = true;
        camera.orthographicSize = Pixels / 2000f;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 5;
        camera.clearFlags = CameraClearFlags.SolidColor;
        // Passthrough behind the panel reads as a mid grey room.
        camera.backgroundColor = new Color(0.42f, 0.44f, 0.46f, 1);
        canvas.worldCamera = camera;
        var target = new RenderTexture(Pixels, Pixels, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        camera.targetTexture = target;

        var card = ui.BuildPodPage() ?? throw new InvalidOperationException("the Pod page was not built");
        var report = new StringBuilder();
        foreach (var (name, state) in States())
        {
            card.ShowFixed(state);
            ui.ShowTab(RecordingListUI.MenuTab.Pod);
            var scroll = ui.PodScroll!;
            Layout(ui, scroll);
            scroll.verticalNormalizedPosition = 1;
            Layout(ui, scroll);
            Check(name, card.transform, report);
            Save(camera, target, Path.Combine(outDir, "podcard-" + name + ".png"));
            var content = scroll.content.rect.height;
            var viewport = scroll.viewport.rect.height;
            report.AppendLine($"{name}: content {content:0} px in a {viewport:0} px viewport");
            if (content > viewport + 1)
            {
                scroll.verticalNormalizedPosition = 0;
                Layout(ui, scroll);
                Save(camera, target, Path.Combine(outDir, "podcard-" + name + "-bottom.png"));
            }
        }

        ui.ShowTab(RecordingListUI.MenuTab.Recordings);
        Layout(ui, ui.ListScroll!);
        Save(camera, target, Path.Combine(outDir, "podcard-recordings-tab.png"));

        File.WriteAllText(Path.Combine(outDir, "podcard-check.txt"), report.ToString());
        Debug.Log("PodCardSnapshot:\n" + report);
    }

    private static void Layout(RecordingListUI ui, ScrollRect scroll)
    {
        for (var i = 0; i < 3; i++)
        {
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(scroll.content);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)scroll.transform);
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)ui.transform);
        }
        foreach (var text in ui.GetComponentsInChildren<TMP_Text>()) text.ForceMeshUpdate();
        Canvas.ForceUpdateCanvases();
    }

    private static void Save(Camera camera, RenderTexture target, string path)
    {
        camera.Render();
        var previous = RenderTexture.active;
        RenderTexture.active = target;
        var image = new Texture2D(Pixels, Pixels, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, Pixels, Pixels), 0, 0);
        image.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(path, image.EncodeToPNG());
        Object.DestroyImmediate(image);
    }

    /// <summary>Rows that overlap the next row in their column, and texts taller than their cell.</summary>
    private static void Check(string name, Transform card, StringBuilder report)
    {
        foreach (var column in card.GetComponentsInChildren<VerticalLayoutGroup>())
        {
            RectTransform? previous = null;
            foreach (Transform child in column.transform)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                var rect = (RectTransform)child;
                if (previous != null && Bottom(previous) < Top(rect) - 0.5f)
                {
                    report.AppendLine($"{name}: OVERLAP {Named(previous)} over {Named(rect)}");
                }
                previous = rect;
            }
        }
        foreach (var text in card.GetComponentsInChildren<TMP_Text>())
        {
            if (text.textWrappingMode == TextWrappingModes.NoWrap) continue;
            var needed = text.preferredHeight;
            if (needed > text.rectTransform.rect.height + 1)
            {
                report.AppendLine($"{name}: CUT {Named(text.transform)} needs {needed:0} px, has {text.rectTransform.rect.height:0}");
            }
        }
    }

    private static float Top(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return corners[1].y * 1000;
    }

    private static float Bottom(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return corners[0].y * 1000;
    }

    private static string Named(Transform t) => t.parent == null ? t.name : Named(t.parent) + "/" + t.name;

    // ---- The states ----

    private static IEnumerable<(string, PodCardState)> States()
    {
        yield return ("a-not-enrolled", NotEnrolled());
        yield return ("b-ready", Ready());
        yield return ("c-blocked", Blocked());
        yield return ("d-long", LongList());
        yield return ("e-unattributed", Unattributed());
        yield return ("f-seal-refused-frozen", SealRefusedFrozen());
        yield return ("g-no-episodes-held", NoEpisodesHeld());
        yield return ("h-storage-low", StorageLow());
        yield return ("i-prestart-warnings", PreStartWarnings());
        yield return ("j-refused-not-in-hand", RefusedNotInHand());
        yield return ("k-recording-controller-lost", RecordingControllerLost());
        yield return ("l-upload-backlog", UploadBacklog());
        yield return ("l-gripper-sensor-silent", GripperSensorSilent());
    }

    // ---- The operator's words, through the real board from the status the rig and the app write ----

    private const string LivePod =
        "\"expected\":true,\"enrolled\":true,\"library\":\"present\",\"open\":true,\"podId\":\"pod-17\"," +
        "\"apkBuildId\":\"sha256:0123456789abcdef0123\",\"floor\":{\"collector\":{\"id\":\"usr_asha\",\"name\":\"Asha Rao\"}}," +
        "\"roles\":[" +
        "{\"role\":\"left_hand_camera\",\"registered\":\"c1\",\"attached\":true,\"port\":\"1-1.3\",\"check\":\"ready\"}," +
        "{\"role\":\"right_hand_camera\",\"registered\":\"c2\",\"attached\":true,\"port\":\"1-1.4\",\"check\":\"ready\"}," +
        "{\"role\":\"left_hand_encoder\",\"registered\":\"e1\",\"attached\":true,\"port\":\"1-1.1\",\"check\":\"ready\"}," +
        "{\"role\":\"right_hand_encoder\",\"registered\":\"e2\",\"attached\":true,\"port\":\"1-1.2\",\"check\":\"ready\"}]";

    private const string LiveFactory =
        "\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null},\"ready\":true,\"blocking\":[]," +
        "\"task\":{\"name\":\"Fold towels\",\"status\":\"active\",\"target\":10,\"done\":6}," +
        "\"progress\":{\"target\":10,\"credited\":6,\"awaiting_review\":2,\"recording\":0,\"processing\":0,\"sealing\":1,\"registering\":0}";

    private static PodCardState Live(string status, PodReadiness readiness, string chip)
    {
        var state = Enrolled(PodScreenBoard.Build("{" + status + "}", null, false, new StreamLiveness(), DateTime.UtcNow),
            Lines(readiness, chip, PodStatusBoard.RecordingFor("Asha Rao")));
        state.RightPct = 12;
        return state;
    }

    /// <summary>(f) The newest attempt's seal refused it for a frozen left controller: shown until the next start.</summary>
    private static PodCardState SealRefusedFrozen() => Live(LivePod + ",\"factory\":{" + LiveFactory + "},\"attempts\":[" +
        "{\"id\":\"20261029_141502\",\"stage\":\"refused\",\"outcome\":\"complete\",\"seal\":\"refused\"," +
        "\"detail\":\"left_controller_pose frozen: it moved 0.31 cm over the episode\"," +
        "\"notice\":{\"code\":\"seal_controller_frozen\",\"who\":\"collector\",\"args\":{\"side\":\"left\"}}}," +
        "{\"id\":\"20261029_140911\",\"stage\":\"reported\",\"outcome\":\"complete\",\"seal\":\"sealed\",\"integrity\":\"pending\"}," +
        "{\"id\":\"20261029_135030\",\"stage\":\"finalized\",\"outcome\":\"complete\",\"seal\":\"sealed\"}," +
        "{\"id\":\"20261029_133012\",\"stage\":\"registered\",\"outcome\":\"complete\",\"seal\":\"sealed\"}]",
        PodReadiness.Ready, "Ready");

    /// <summary>(g) No episodes free: four held by attempts awaiting review; Start is not offered.</summary>
    private static PodCardState NoEpisodesHeld() => Live(LivePod + ",\"factory\":{" + LiveFactory + ",\"admissible\":0," +
        "\"start_refusal\":{\"reason\":\"no_episodes\",\"checks\":[],\"held\":4," +
        "\"notice\":{\"code\":\"no_episodes_held\",\"who\":\"floor_manager\",\"args\":{\"held\":\"4\"}}}}," +
        "\"attempts\":[{\"id\":\"20261029_141502\",\"stage\":\"sealing\",\"outcome\":\"complete\",\"seal\":\"pending\",\"sealing_seconds\":190}," +
        "{\"id\":\"20261029_140911\",\"stage\":\"reported\",\"outcome\":\"complete\",\"seal\":\"sealed\",\"integrity\":\"pending\"}]",
        PodReadiness.Refused, "Refused");

    /// <summary>(l) Pod 11 on 2026-11-22: an upload moving at 1.8 MiB/s with 13 sealed takes (6.8 GB) queued behind it.</summary>
    private static PodCardState UploadBacklog() => Live(LivePod + ",\"factory\":{" + LiveFactory + "}," +
        "\"uplink\":{\"state\":\"uploading\",\"upload\":{\"artifact\":\"artifact_4ea37989eab5\",\"episode\":\"20261123_001348\"," +
        "\"size_bytes\":489800000,\"transferred_bytes\":201326592,\"parts\":3,\"rate_mib_s\":1.8,\"waiting_for\":null}," +
        "\"queue\":{\"artifacts\":13,\"bytes\":6800000000}," +
        "\"notice\":{\"code\":\"upload_backlog\",\"who\":\"collector\",\"args\":{\"count\":\"13\",\"gb\":\"6.8\"}}}," +
        "\"attempts\":[{\"id\":\"20261123_001348\",\"stage\":\"sealed\",\"outcome\":\"complete\",\"seal\":\"sealed\"}," +
        "{\"id\":\"20261123_000440\",\"stage\":\"sealed\",\"outcome\":\"complete\",\"seal\":\"sealed\"}]",
        PodReadiness.Ready, "Ready");

    /// <summary>(h) Not enough space, with the rig's own numbers and the uploads that will free it.</summary>
    private static PodCardState StorageLow() => Live(LivePod + ",\"factory\":{" + LiveFactory + "}," +
        // The real defaults: 600 s at the worst case, 1.32 GB episode + 0.93 GB OpenQuestCapture session,
        // plus two episodes still waiting for their seal (1.1 GB each).
        "\"native\":{\"state\":\"open\",\"storage\":{\"free_bytes\":3000000000,\"required_bytes\":4448800000," +
        "\"episode_bytes\":1320000000,\"session_bytes\":928800000,\"backlog_bytes\":2200000000,\"backlog_episodes\":2," +
        "\"held\":{\"unreported\":{\"attempts\":2},\"awaiting_factory\":{\"attempts\":3}}}}",
        PodReadiness.Refused, "Refused");

    /// <summary>(i) Ready, with the warn-level checks before Start: a controller asleep, one low, 72 Hz, tight storage, held episodes.</summary>
    private static PodCardState PreStartWarnings() => Live(LivePod + ",\"factory\":{" + LiveFactory + ",\"admissible\":2," +
        "\"warnings\":[{\"code\":\"slots_held\",\"who\":\"floor_manager\",\"args\":{\"held\":\"2\"}}]}," +
        "\"headset\":{\"displayHz\":72.0,\"controllers\":[{\"side\":\"left\",\"connected\":true,\"tracked\":false,\"percent\":64}," +
        "{\"side\":\"right\",\"connected\":true,\"tracked\":true,\"percent\":12}]}," +
        "\"native\":{\"state\":\"open\",\"storage\":{\"free_bytes\":3600000000,\"required_bytes\":2248800000,\"backlog_bytes\":0,\"backlog_episodes\":0," +
        "\"held\":{\"unreported\":{\"attempts\":1}}}}",
        PodReadiness.Ready, "Ready");

    /// <summary>(j) A start the rig's tracking law refused (sx#4210): a controller set down, out of reach.</summary>
    private static PodCardState RefusedNotInHand() => Live(LivePod + ",\"factory\":{" + LiveFactory + "}," +
        "\"tracking\":{\"start_refusal\":{\"kind\":\"controllers_not_in_hand\",\"detail\":\"left controller is 1.9 m from the headset\"," +
        "\"notice\":{\"code\":\"controllers_not_in_hand\",\"who\":\"collector\",\"args\":{}}},\"controllers_lost\":[]}," +
        "\"lastRefusal\":{\"code\":\"controllers_not_in_hand\",\"message\":\"left controller is 1.9 m from the headset\"," +
        "\"detail\":{\"kind\":\"controllers_not_in_hand\",\"notice\":{\"code\":\"controllers_not_in_hand\",\"who\":\"collector\",\"args\":{}}}}",
        PodReadiness.Refused, "Refused");

    /// <summary>
    /// (l) The right gripper sensor fell silent twice in the last minute (Pod 19, OQC #44): the
    /// rig's encoders.start_refusal, so Start is not offered and the card names the cable.
    /// </summary>
    private static PodCardState GripperSensorSilent() => Live(LivePod + ",\"factory\":{" + LiveFactory + "}," +
        "\"encoders\":{\"start_refusal\":{\"kind\":\"gripper_sensor_silent\",\"side\":\"right\"," +
        "\"detail\":\"right encoder: the gripper sensor stopped sending 2 times in the last 60 s (last for 1.4 s, 8 s ago); reseat the sensor's cable at the sensor and at the hub\"," +
        "\"notice\":{\"code\":\"gripper_sensor_silent\",\"who\":\"collector\",\"args\":{\"side\":\"right\",\"part\":\"gripper_sensor\"}}}," +
        "\"sides\":[{\"side\":\"left\",\"last_sample_age_ms\":12,\"gaps_recent\":0,\"encoder_ok\":true}," +
        "{\"side\":\"right\",\"last_sample_age_ms\":9,\"gaps_recent\":2,\"last_gap_ms\":1400,\"last_gap_ago_ms\":8000,\"encoder_ok\":false}]}",
        PodReadiness.Refused, "Refused");

    /// <summary>(k) Recording, the left controller out of hand for 3 s: the banner says so, recording goes on.</summary>
    private static PodCardState RecordingControllerLost() => Recording(Live(LivePod + ",\"episodeActive\":true,\"factory\":{" + LiveFactory + "}," +
        "\"native\":{\"state\":\"recording\",\"episode\":{\"episode_id\":\"20261115_120000\"}}," +
        "\"tracking\":{\"start_refusal\":null,\"controllers_lost\":[{\"code\":\"controller_lost\",\"who\":\"collector\",\"args\":{\"side\":\"left\"}}]}",
        PodReadiness.Ready, "Recording"));

    private static PodCardState Recording(PodCardState state)
    {
        state.Recording = true;
        return state;
    }

    /// <summary>(a) A fresh install: not enrolled (it records nothing until it is), nothing plugged in; the real boards' words.</summary>
    private static PodCardState NotEnrolled()
    {
        const string status =
            "{\"expected\":false,\"configured\":false,\"enrolled\":false,\"headsetSerial\":null," +
            "\"tailscaleIp\":null,\"apkBuildId\":null,\"library\":\"unknown\",\"open\":false,\"openError\":null," +
            "\"episodeActive\":false,\"usb\":{\"present\":0,\"attached\":[],\"awaitingPermission\":[],\"attachErrors\":{}}," +
            "\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null,\"deviceReport\":null," +
            "\"startupErrors\":{},\"lastCheck\":null,\"native\":null,\"lastRefusal\":null}";
        return new PodCardState
        {
            Lines = PodStatusBoard.Lines(status, null, false),
            Screen = PodScreenBoard.Build(status, null, false, new StreamLiveness(), DateTime.UtcNow),
            HeadsetPct = 81,
            LeftPct = null,
            RightPct = null,
            SetupAvailable = true,
            SetupMessage = PodStatusBoard.SetupStatus(null, 999, false, false),
            FactoryUrl = PodSetupJson.DefaultFactoryUrl,
            AuthUrl = PodSetupJson.DefaultAuthUrl,
            PhonePhase = CompanionPhase.NotPaired,
            Phone = CompanionCard.Describe(CompanionPhase.NotPaired, "", "", "", ""),
        };
    }

    private static PodStatusLines Lines(PodReadiness readiness, string chip, string collector) =>
        new("Pod: pod-17", "Collector: " + collector, "Task: Fold towels", "Rig: open", "Last refusal: none", "",
            "pod-17", readiness, chip, false);

    private static PodDeviceRow Device(string name, string value, string pill, PodTone tone, string hint = "") =>
        new(name, value, pill, tone, hint, "", "", "", PodDeviceState.Ready);

    private static PodCardState Enrolled(PodScreen screen, PodStatusLines lines) => new()
    {
        Lines = lines,
        Screen = screen,
        HeadsetPct = 66,
        LeftPct = 80,
        RightPct = 24,
        Enrolled = true,
        SetupAvailable = true,
        FactoryUrl = PodSetupJson.DefaultFactoryUrl,
        AuthUrl = PodSetupJson.DefaultAuthUrl,
        PhonePhase = CompanionPhase.Connected,
        Phone = CompanionCard.Describe(CompanionPhase.Connected, "Pixel 8", "Asha", "pod-17", ""),
    };

    /// <summary>(b) Enrolled and ready: four devices, a collection at 1 of 2, three episodes at different stages.</summary>
    private static PodCardState Ready()
    {
        var screen = new PodScreen
        {
            Blocker = new PodBlocker("Ready to record", "Press the menu button, or hold the right gripper button for 2 s, to start", PodTone.Good),
            Task = "Fold towels (bimanual, 2 episodes)",
            Progress = "1 of 2 credited",
            Next = "Record episode 2",
            Link = "connected, heard 3 s ago",
            LinkTone = PodTone.Good,
            Credited = 1,
            Requested = 2,
        };
        screen.Devices.Add(Device("Left camera", "port 1-1.3", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Right camera", "port 1-1.4", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Left encoder", "port 1-1.1", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Right encoder", "port 1-1.2", "Ready", PodTone.Good));
        screen.Episodes.Add(new PodEpisodeRow("ep-0003", 1, -1, "Sealing", PodTone.Warning, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0002", 3, -1, "Awaiting review", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In Catalog", PodTone.Good, ""));
        return Enrolled(screen, Lines(PodReadiness.Ready, "Ready", PodStatusBoard.RecordingFor("Asha Rao")));
    }

    /// <summary>(c) Blocked with a long reason, a silent encoder with its fix, a missing camera and Check's answer.</summary>
    private static PodCardState Blocked()
    {
        var screen = new PodScreen
        {
            Blocker = new PodBlocker(
                "The rig's last check failed: right encoder sends nothing on its stream after 2.5 s; the left camera is not on USB",
                "Another app holds the encoder: replug it and choose OpenQuestCapture when Android asks, then press Check",
                PodTone.Error),
            Task = "Fold towels (bimanual, 2 episodes)",
            Progress = "1 of 2 credited",
            Next = "Fix the rig, then record episode 2",
            Link = "connected, heard 3 s ago",
            LinkTone = PodTone.Good,
            StartBlocked = true,
            Credited = 1,
            Requested = 2,
        };
        screen.Devices.Add(Device("Left camera", "not on USB", "Missing", PodTone.Error, "Plug it into the hub"));
        screen.Devices.Add(Device("Right camera", "port 1-1.4", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Left encoder", "port 1-1.1", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Right encoder", "port 1-1.2, 0 bytes received", "Silent", PodTone.Warning,
            "Another app holds it: replug it and choose OpenQuestCapture when Android asks"));
        screen.Episodes.Add(new PodEpisodeRow("ep-0002", 1, 1, "Seal refused", PodTone.Error,
            "right encoder: stream ended early (2.1 s of 30 s); replug it before the next start"));
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In Catalog", PodTone.Good, ""));
        var state = Enrolled(screen, Lines(PodReadiness.Refused, "Refused", PodStatusBoard.RecordingFor("Asha Rao")));
        state.PanelTone = PodTone.Error;
        state.PanelHeadline = "Check failed: right encoder sends nothing; left camera is not on USB";
        return state;
    }

    /// <summary>(e) Enrolled, no phone paired: Start is offered and the take records unattributed; the real board's words.</summary>
    private static PodCardState Unattributed()
    {
        const string status =
            "{\"expected\":true,\"enrolled\":true,\"library\":\"present\",\"open\":true,\"podId\":\"pod-17\"," +
            "\"apkBuildId\":\"sha256:0123456789abcdef0123\",\"floor\":{\"collector\":null}," +
            "\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null}}}";
        var state = Enrolled(PodScreenBoard.Build(status, null, false, new StreamLiveness(), DateTime.UtcNow),
            Lines(PodReadiness.Ready, "Ready", PodStatusBoard.Unattributed));
        state.Phone = CompanionCard.Describe(CompanionPhase.NotPaired, "", "", "pod-17", "");
        state.PhonePhase = CompanionPhase.NotPaired;
        return state;
    }

    /// <summary>(d) A long episode list that needs scrolling.</summary>
    private static PodCardState LongList()
    {
        var screen = new PodScreen
        {
            Blocker = new PodBlocker("Ready to record", "Press the menu button, or hold the right gripper button for 2 s, to start", PodTone.Good),
            Task = "Stack cups (right hand, 12 episodes)",
            Progress = "7 of 12 credited, 1 awaiting review",
            Next = "Record episode 9",
            Link = "connected, heard 1 s ago",
            LinkTone = PodTone.Good,
            Credited = 7,
            Requested = 12,
        };
        screen.Devices.Add(Device("Left camera", "port 1-1.3", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Right camera", "port 1-1.4", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Left encoder", "port 1-1.1", "Ready", PodTone.Good));
        screen.Devices.Add(Device("Right encoder", "port 1-1.2", "Ready", PodTone.Good));
        screen.Episodes.Add(new PodEpisodeRow("ep-0008", 0, -1, "Recording", PodTone.Warning, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0007", 2, -1, "Reported", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0006", 3, -1, "Awaiting review", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0005", 3, 3, "Review failed", PodTone.Error,
            "Reviewer: the right hand leaves the camera's view for most of the episode"));
        screen.Episodes.Add(new PodEpisodeRow("ep-0004", 5, -1, "Delivered", PodTone.Good, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0003", 6, -1, "In Catalog", PodTone.Good, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0002", 6, -1, "In Catalog", PodTone.Good, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In Catalog", PodTone.Good, ""));
        return Enrolled(screen, Lines(PodReadiness.Ready, "Ready", PodStatusBoard.RecordingFor("Asha Rao")));
    }
}
