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
        yield return ("e-signed-out", SignedOut());
    }

    /// <summary>(a) A fresh install: not enrolled, Local only off, nothing plugged in; the real boards' words.</summary>
    private static PodCardState NotEnrolled()
    {
        const string status =
            "{\"expected\":false,\"configured\":false,\"enrolled\":false,\"localOnly\":false,\"headsetSerial\":null," +
            "\"tailscaleIp\":null,\"apkBuildId\":null,\"library\":\"unknown\",\"open\":false,\"openError\":null," +
            "\"episodeActive\":false,\"usb\":{\"present\":0,\"attached\":[],\"awaitingPermission\":[],\"attachErrors\":{}}," +
            "\"podId\":null,\"registeredRig\":null,\"seals\":null,\"uplink\":null,\"floor\":null,\"deviceReport\":null," +
            "\"startupErrors\":{},\"lastCheck\":null,\"native\":null,\"lastRefusal\":null}";
        return new PodCardState
        {
            Lines = PodStatusBoard.Lines(status, null, false, false),
            Screen = PodScreenBoard.Build(status, null, false, new StreamLiveness(), DateTime.UtcNow),
            HeadsetPct = 81,
            LeftPct = null,
            RightPct = null,
            SetupAvailable = true,
            SetupMessage = PodStatusBoard.SetupStatus(null, 999, false, false, false),
            FactoryUrl = PodSetupJson.DefaultFactoryUrl,
            AuthUrl = PodSetupJson.DefaultAuthUrl,
            PhonePhase = CompanionPhase.NotPaired,
            Phone = CompanionCard.Describe(CompanionPhase.NotPaired, "", "", "", ""),
        };
    }

    private static PodStatusLines Lines(PodReadiness readiness, string chip, string collector) =>
        new("Pod: pod-17", "Collector: " + collector, "Task: Fold towels", "Rig: open", "Last refusal: none", "",
            "pod-17", readiness, chip, false, "", "");

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
        SignedIn = true,
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
            Blocker = new PodBlocker("Ready to record", "Press the menu button to start", PodTone.Good),
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
        screen.Episodes.Add(new PodEpisodeRow("ep-0002", 3, -1, "In review", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In catalog", PodTone.Good, ""));
        return Enrolled(screen, Lines(PodReadiness.Ready, "Ready", "Asha Rao (signed in)"));
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
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In catalog", PodTone.Good, ""));
        var state = Enrolled(screen, Lines(PodReadiness.Refused, "Refused", "Asha Rao (signed in)"));
        state.PanelTone = PodTone.Error;
        state.PanelHeadline = "Check failed: right encoder sends nothing; left camera is not on USB";
        return state;
    }

    /// <summary>(e) Enrolled, nobody signed in: Start is blocked with "Sign in to record"; the real board's words.</summary>
    private static PodCardState SignedOut()
    {
        const string status =
            "{\"expected\":true,\"enrolled\":true,\"localOnly\":false,\"library\":\"present\",\"open\":true,\"podId\":\"pod-17\"," +
            "\"apkBuildId\":\"sha256:0123456789abcdef0123\",\"floor\":{\"operator\":{\"signed_in\":false}}," +
            "\"factory\":{\"link\":{\"ok\":true,\"heard_seconds_ago\":3,\"error\":null}}}";
        var state = Enrolled(PodScreenBoard.Build(status, null, false, new StreamLiveness(), DateTime.UtcNow),
            Lines(PodReadiness.NotReady, "Not ready", "nobody signed in"));
        state.SignedIn = false;
        return state;
    }

    /// <summary>(d) A long episode list that needs scrolling.</summary>
    private static PodCardState LongList()
    {
        var screen = new PodScreen
        {
            Blocker = new PodBlocker("Ready to record", "Press the menu button to start", PodTone.Good),
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
        screen.Episodes.Add(new PodEpisodeRow("ep-0007", 2, -1, "Reporting", PodTone.Warning, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0006", 3, -1, "In review", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0005", 3, 3, "Rejected", PodTone.Error,
            "Reviewer: the right hand leaves the camera's view for most of the episode"));
        screen.Episodes.Add(new PodEpisodeRow("ep-0004", 5, -1, "Finalizing", PodTone.Neutral, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0003", 6, -1, "In catalog", PodTone.Good, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0002", 6, -1, "In catalog", PodTone.Good, ""));
        screen.Episodes.Add(new PodEpisodeRow("ep-0001", 6, -1, "In catalog", PodTone.Good, ""));
        return Enrolled(screen, Lines(PodReadiness.Ready, "Ready", "Asha Rao (signed in)"));
    }
}
