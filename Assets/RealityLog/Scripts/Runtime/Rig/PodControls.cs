# nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The operator's controls, one at a time, for the Pod card and the paired phone alike:
    /// <b>Check</b> (<see cref="RigCoordinator.CheckNow"/>, sx_rig_check), <b>Start</b> and
    /// <b>Stop</b> (the left menu button's path, <see cref="StartOrStop"/>), and <b>End
    /// collection</b> (<see cref="RigCoordinator.EndCollectionNow"/>, sx-pod's End collection;
    /// each screen asks the operator to confirm first). Each ends in one
    /// <see cref="PodControlResult"/>, raised on the main thread as <see cref="Finished"/>, which
    /// the card shows and the phone companion sends to the phone, whichever screen it was
    /// pressed on. It also reads the Touch controllers' charge once a second.
    /// </summary>
    public sealed class PodControls : MonoBehaviour
    {
        private const float ControllerReadSeconds = 1f;

        public static PodControls? Instance { get; private set; }

        /// <summary>
        /// The menu button's start or stop, registered by the HTTP controller (another assembly):
        /// (start, done) returns why it did not begin, or null; done hears (ok, words).
        /// </summary>
        public static Func<bool?, Action<bool, string>?, string?>? StartOrStop;

        /// <summary>The control running now, if any.</summary>
        public PodControl? Running { get; private set; }

        /// <summary>The last control's result, and when it came (unscaled time).</summary>
        public PodControlResult? Last { get; private set; }
        public float LastAt { get; private set; } = float.NegativeInfinity;

        /// <summary>The Touch controllers' charge, as last read.</summary>
        public ControllerCharge Controllers { get; private set; } = ControllerCharge.Unknown;

        /// <summary>A control ended (main thread).</summary>
        public event Action<PodControlResult>? Finished;

        private readonly ConcurrentQueue<PodControlResult> done = new();
        private float lastControllerRead = float.NegativeInfinity;
        private string lastControllerLog = "";
        private readonly ControllerBattery battery = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Instance is not null) return;
            var go = new GameObject(nameof(PodControls));
            Instance = go.AddComponent<PodControls>();
            DontDestroyOnLoad(go);
        }

        /// <summary>
        /// Runs <paramref name="control"/> (main thread). End collection runs only after the
        /// screen that asked has confirmed it. A refusal before it starts is a result too.
        /// </summary>
        public void Run(PodControl control)
        {
            if (Running is PodControl busy)
            {
                Finish(new PodControlResult(control, false, PodControlWords.Label(busy) + " is still running"));
                return;
            }
            var recording = FindFirstObjectByType<RecordingManager>() is { State: not RecordingState.Idle };
            var rig = RigCoordinator.Instance;
            Debug.Log($"[{Constants.LOG_TAG}] PodControls: {control} asked (recording={recording})");
            switch (control)
            {
                case PodControl.Check:
                    if (recording)
                    {
                        Finish(new PodControlResult(control, false, "an episode is recording; check between episodes"));
                        return;
                    }
                    if (rig is not { IsExpected: true })
                    {
                        Finish(new PodControlResult(control, false, "no rig on this headset to check"));
                        return;
                    }
                    Running = control;
                    Task.Run(() => done.Enqueue(Guarded(control, rig.CheckNow)));
                    return;
                case PodControl.EndCollection:
                    if (recording)
                    {
                        Finish(new PodControlResult(control, false, "an episode is recording; stop it first"));
                        return;
                    }
                    if (rig == null)
                    {
                        Finish(new PodControlResult(control, false, "no rig on this headset"));
                        return;
                    }
                    Running = control;
                    Task.Run(() => done.Enqueue(Guarded(control, rig.EndCollectionNow)));
                    return;
                default:
                    if (StartOrStop is not { } startOrStop)
                    {
                        Finish(new PodControlResult(control, false, "the recorder is not running"));
                        return;
                    }
                    Running = control;
                    var refused = startOrStop(control == PodControl.Start,
                        (ok, words) => done.Enqueue(new PodControlResult(control, ok, words)));
                    if (refused != null)
                    {
                        Running = null;
                        Finish(new PodControlResult(control, false, refused));
                    }
                    return;
            }
        }

        private static PodControlResult Guarded(PodControl control, Func<PodControlResult> run)
        {
            try
            {
                return run();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] PodControls: {control} failed: {ex}");
                return new PodControlResult(control, false, ex.Message);
            }
        }

        private void Update()
        {
            while (done.TryDequeue(out var result))
            {
                Running = null;
                // Start and Stop cue on their own path; a check or an end is heard here.
                if (result.Control is PodControl.Check or PodControl.EndCollection)
                {
                    RigCoordinator.Instance?.QueueCue(!result.Ok ? RigCues.Cue.Refused
                        : result.Control == PodControl.Check ? RigCues.Cue.Sealed : RigCues.Cue.Stop);
                }
                Publish(result);
            }
            if (Time.unscaledTime - lastControllerRead >= ControllerReadSeconds)
            {
                lastControllerRead = Time.unscaledTime;
                ReadControllers();
            }
        }

        /// <summary>A refusal before anything ran: buzzed, shown and sent like any result.</summary>
        private void Finish(PodControlResult result)
        {
            RigCoordinator.Instance?.QueueCue(RigCues.Cue.Refused);
            Publish(result);
        }

        private void Publish(PodControlResult result)
        {
            Last = result;
            LastAt = Time.unscaledTime;
            Debug.Log($"[{Constants.LOG_TAG}] PodControls: {result.Control} -> ok={result.Ok}: {result.Message}");
            Finished?.Invoke(result);
        }

        /// <summary>
        /// The controllers' charge (<see cref="ControllerBattery"/>: Unity XR's batteryLevel, else
        /// OVRInput, which answers 0 under OpenXR); a controller no source reads is unknown.
        /// </summary>
        private void ReadControllers()
        {
            try
            {
                Controllers = battery.Read();
                var log = PodControlWords.Percent(Controllers.Left) + " / " + PodControlWords.Percent(Controllers.Right);
                if (log != lastControllerLog)
                {
                    lastControllerLog = log;
                    Debug.Log($"[{Constants.LOG_TAG}] PodControls: controller battery {log}");
                }
            }
            catch (Exception ex)
            {
                Controllers = ControllerCharge.Unknown;
                Debug.LogWarning($"[{Constants.LOG_TAG}] PodControls: controller battery: {ex.Message}");
            }
        }
    }
}
