// Admission regressions against the production monitor. The SDK seam supplies explicit
// tracking states; this is host branch coverage, not a substitute for Quest acceptance.
#nullable enable
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using RealityLog;

static class HeadTrackingMonitorHarness
{
    static int failures;
    static int checks;
    static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    static void Expect(bool ok, string what)
    {
        checks++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }
    static void Healthy()
    {
        typeof(HeadTrackingMonitor).GetMethod("ResetStatics", PrivateStatic)!.Invoke(null, null);
        OVRPlugin.Reset();
        OVRInput.LeftConnected = OVRInput.RightConnected = true;
    }
    static void Refuses(Action change, string contains, string what)
    {
        Healthy();
        HeadTrackingMonitor.RefreshNow(); // previously good cached state must not win
        change();
        HeadTrackingMonitor.RefreshNow();
        var reason = HeadTrackingMonitor.StartRefusal;
        Expect(reason != null && reason.Contains(contains), what);
    }
    static int Main()
    {
        Healthy();
        Expect(HeadTrackingMonitor.StartRefusal != null, "no samples refuse a start");
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "fresh, connected, fully tracked stationary poses admit");

        Refuses(() => OVRPlugin.Position[1] = false, "left controller", "refresh rejects lost left position despite orientation tracking");
        Refuses(() => OVRPlugin.Orientation[1] = false, "left controller", "left position alone does not admit");
        Refuses(() => OVRPlugin.Position[2] = false, "right controller", "refresh rejects lost right position");
        Refuses(() => OVRPlugin.Orientation[2] = false, "right controller", "right position alone does not admit");
        Refuses(() => OVRInput.LeftConnected = false, "left controller", "left disconnected with retained valid pose refuses");
        Refuses(() => OVRInput.RightConnected = false, "right controller", "right disconnected with retained valid pose refuses");
        Refuses(() => OVRPlugin.Position[0] = false, "head", "fresh head timestamp cannot excuse untracked position");
        Refuses(() => OVRPlugin.Orientation[0] = false, "head", "fresh head timestamp cannot excuse untracked orientation");

        foreach (var invalid in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            Refuses(() => OVRPlugin.Times[0] = invalid, "head", $"invalid head source time {invalid} refuses");
            Refuses(() => OVRPlugin.Times[1] = invalid, "left controller", $"invalid left source time {invalid} refuses");
            Refuses(() => OVRPlugin.Times[2] = invalid, "right controller", $"invalid right source time {invalid} refuses");
            Refuses(() => OVRPlugin.BodyTime = invalid, "body", $"invalid body source time {invalid} refuses");
        }

        Refuses(() => OVRPlugin.Times[0] = OVRPlugin.Now - .101, "head", "head source older than100 ms refuses despite new observed timestamp");
        Refuses(() => OVRPlugin.Times[1] = OVRPlugin.Now - .101, "left controller", "left source older than100 ms refuses");
        Refuses(() => OVRPlugin.Times[2] = OVRPlugin.Now - .101, "right controller", "right source older than100 ms refuses");
        Refuses(() => OVRPlugin.BodyTime = OVRPlugin.Now - .101, "body", "body source older than100 ms refuses");
        Refuses(() => OVRPlugin.BodyActive = false, "body", "inactive body source refuses");
        Refuses(() => OVRPlugin.BodyHasJoints = false, "body", "incomplete body result refuses without throwing");
        Refuses(() => OVRPlugin.BodyTime = OVRPlugin.Now + .101, "body", "body predicted beyond existing100 ms allowance refuses");
        Refuses(() => OVRPlugin.Times[1] = OVRPlugin.Now + .101, "left controller", "controller source time beyond bounded future allowance refuses");
        Refuses(() => OVRPlugin.Now = double.NaN, "head", "invalid source clock does not admit");

        Healthy();
        OVRPlugin.BodyTime = OVRPlugin.Now + .05;
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "normal predicted Step.Render body timestamp remains accepted");
        Healthy();
        for (var i = 0; i < 3; i++) OVRPlugin.Times[i] += .000001;
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "tiny negative immediate source age does not refuse for clock comparison skew");
        Healthy();
        for (var i = 0; i < 3; i++) OVRPlugin.Times[i] -= .09;
        OVRPlugin.BodyTime -= .09;
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "all source samples within100 ms remain accepted");

        Healthy();
        HeadTrackingMonitor.RefreshNow();
        typeof(HeadTrackingMonitor).GetField("controllerSampleTicks", PrivateStatic)!.SetValue(null,
            Stopwatch.GetTimestamp() - Stopwatch.Frequency / 5);
        Expect(HeadTrackingMonitor.StartRefusal != null, "cached snapshot itself ages while the main thread is stalled");
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "fresh complete refresh recovers without waiting for Update");

        Healthy();
        OVRPlugin.AfterBodyRead = () => OVRPlugin.Now += .2;
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal != null, "time spent sampling other SDK sources ages the earlier head and controller reads");

        Healthy();
        HeadTrackingMonitor.RefreshNow();
        OVRPlugin.Now += .02;
        for (var i = 0; i < 3; i++) OVRPlugin.Times[i] = OVRPlugin.Now;
        OVRPlugin.BodyTime = OVRPlugin.Now;
        HeadTrackingMonitor.RefreshNow();
        Expect(HeadTrackingMonitor.StartRefusal == null, "no movement or changed coordinates are required for tracked poses");

        var reads = OVRPlugin.Reads;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { _ = HeadTrackingMonitor.StartRefusal; _ = HeadTrackingMonitor.StatusJson(""); }
            catch (Exception ex) { error = ex; }
        });
        thread.Start(); thread.Join();
        Expect(error == null && OVRPlugin.Reads == reads, "HTTP/status reads do not invoke the SDK on a worker thread");

        Healthy();
        var monitor = new HeadTrackingMonitor();
        var update = typeof(HeadTrackingMonitor).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance)!;
        update.Invoke(monitor, null);
        OVRPlugin.Position[2] = false;
        update.Invoke(monitor, null);
        Expect(HeadTrackingMonitor.StartRefusal?.Contains("right controller") == true, "normal Update publishes the same complete readiness decision");
        Expect(OVRPlugin.Times[0] == 100 && OVRPlugin.Times[1] == 100 && OVRPlugin.Times[2] == 100 && OVRPlugin.BodyTime == 100,
            "admission leaves all input sample timestamps unchanged");
        Console.WriteLine($"{checks - failures}/{checks} admission checks passed");
        return failures == 0 ? 0 : 1;
    }
}

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Class)] public sealed class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int value) {} }
    public enum RuntimeInitializeLoadType { SubsystemRegistration, AfterSceneLoad }
    [AttributeUsage(AttributeTargets.Method)] public sealed class RuntimeInitializeOnLoadMethod : Attribute { public RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType value) {} }
    public class MonoBehaviour { protected static void DontDestroyOnLoad(object value) {} }
    public class GameObject { public GameObject(string name) {} public T AddComponent<T>() where T : new() => new T(); }
    public static class Time { public static int frameCount; }
    public static class Mathf { public static float Sqrt(float value) => (float)Math.Sqrt(value); }
    public static class Debug { public static void LogWarning(string text) {} }
}
namespace RealityLog.Common { public static class Constants { public const string LOG_TAG = "test"; } }

public static class OVRManager
{
    public static bool isHmdPresent = true, hasVrFocus = true, hasInputFocus = true;
#pragma warning disable CS0067
    public static event Action? HMDLost, HMDAcquired, TrackingLost, TrackingAcquired, VrFocusLost, VrFocusAcquired, InputFocusLost, InputFocusAcquired;
#pragma warning restore CS0067
}
public static class OVRInput
{
    public enum Controller { LTouch, RTouch }
    public static bool LeftConnected = true, RightConnected = true;
    public static bool IsControllerConnected(Controller controller) => controller == Controller.LTouch ? LeftConnected : RightConnected;
}
public static class OVRPlugin
{
    public enum Node { Head, ControllerLeft, ControllerRight }
    public enum Step { Render }
    public enum BodyJointSet { FullBody }
    [Flags] public enum SpaceLocationFlags { PositionValid = 1, OrientationValid = 2 }
    public struct Vector3f { public float x, y, z; }
    public struct Quatf { public float x, y, z, w; }
    public struct Posef { public Vector3f Position; public Quatf Orientation; }
    public struct PoseStatef { public double Time; public Posef Pose; }
    public struct BodyJointLocation { public SpaceLocationFlags LocationFlags; public Posef Pose; }
    public struct BodyState { public BodyJointLocation[] JointLocations; public double Time; public float Confidence; }
    public static double Now, BodyTime;
    public static double[] Times = new double[3];
    public static bool[] Position = new bool[3], Orientation = new bool[3];
    public static bool BodyActive, BodyHasJoints;
    public static Action? AfterBodyRead;
    public static int Reads;
    public static void Reset()
    {
        Now = BodyTime = 100;
        for (int i = 0; i < 3; i++) { Times[i] = 100; Position[i] = Orientation[i] = true; }
        BodyActive = BodyHasJoints = true;
        AfterBodyRead = null;
        Reads = 0;
    }
    public static PoseStatef GetNodePoseStateImmediate(Node node)
    {
        Reads++;
        return new PoseStatef { Time = Times[(int)node], Pose = new Posef { Orientation = new Quatf { w = 1 } } };
    }
    public static bool GetNodePositionTracked(Node node) { Reads++; return Position[(int)node]; }
    public static bool GetNodeOrientationTracked(Node node) { Reads++; return Orientation[(int)node]; }
    public static double GetTimeInSeconds() { Reads++; return Now; }
    public static bool GetBodyState4(Step step, BodyJointSet joints, ref BodyState body)
    {
        Reads++;
        if (!BodyActive) return false;
        body.Time = BodyTime;
        body.Confidence = 1;
        body.JointLocations = new BodyJointLocation[BodyHasJoints ? 84 : 0];
        AfterBodyRead?.Invoke();
        return true;
    }
}
