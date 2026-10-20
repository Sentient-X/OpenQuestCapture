# nullable enable

using System;
using System.Collections.Concurrent;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The headset's side of the phone companion: packages/sx-companion-android's
    /// CompanionHeadset (the GATT central, handshake, keys, framing and the Keystore pairing
    /// store, in Assets/Plugins/Android/sx-companion-release.aar), driven through JNI. The
    /// protocol is the library's alone; this class passes what the headset knows in, and queues
    /// the library's callbacks (they arrive on its own thread) for <see cref="Drain"/> on the
    /// main thread. Null where the library is absent (the editor): the Pod card then shows no
    /// Phone section.
    /// </summary>
    public sealed class CompanionLink : IDisposable
    {
        public const string HeadsetClassName = "io.sentientx.sx.companion.CompanionHeadset";
        public const string ListenerInterfaceName = "io.sentientx.sx.companion.CompanionHeadset$Listener";
        public const string StatusBuilderClassName = "io.sentientx.sx.companion.HeadsetStatusBuilder";

        private readonly AndroidJavaObject headset;
        private readonly ConcurrentQueue<(string kind, string a, string b)> events;

        /// <summary>The paired phone, read from the store at open and after each change (the store is Keystore-wrapped).</summary>
        public bool Paired { get; private set; }
        public string PhoneName { get; private set; } = "";
        public string OperatorName { get; private set; } = "";

        private CompanionLink(AndroidJavaObject headset, ConcurrentQueue<(string, string, string)> events)
        {
            this.headset = headset;
            this.events = events;
            RefreshPairing();
        }

        /// <summary>The library on this activity, or null (with the reason logged) when it cannot load.</summary>
        public static CompanionLink? Open()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                var events = new ConcurrentQueue<(string, string, string)>();
                using var player = new AndroidJavaClass(Constants.UNITY_PLAYER_CLASS_NAME);
                using var activity = player.GetStatic<AndroidJavaObject>(Constants.UNITY_PLAYER_CURRENT_ACTIVITY_VARIABLE_NAME);
                return new CompanionLink(new AndroidJavaObject(HeadsetClassName, activity, new Listener(events)), events);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CompanionLink: the companion library did not load: {ex.Message}");
                return null;
            }
#else
            return null;
#endif
        }

        /// <summary>
        /// Applies the library's queued callbacks (main thread): its states and errors to
        /// <paramref name="tracker"/>, the phone's asks to the handlers. A phone that no longer
        /// holds this pairing is forgotten here too.
        /// </summary>
        public void Drain(CompanionLinkTracker tracker, Action signInAsk, Action signOutAsk)
        {
            while (events.TryDequeue(out var e))
            {
                switch (e.kind)
                {
                    case "state":
                        if (e.a is "connected" or "idle") RefreshPairing();
                        tracker.OnState(e.a, Paired);
                        break;
                    case "paired":
                        RefreshPairing();
                        break;
                    case "error":
                        Debug.LogWarning($"[{Constants.LOG_TAG}] CompanionLink: {e.a}: {e.b}");
                        if (tracker.OnError(e.a, e.b))
                        {
                            headset.Call("forget");
                            RefreshPairing();
                        }
                        break;
                    case "sign_in":
                        signInAsk();
                        break;
                    case "sign_out":
                        signOutAsk();
                        break;
                }
            }
        }

        /// <summary>
        /// Starts pairing with the phone that shows <paramref name="qr"/> (sxc1:...); the Hello
        /// names the pod (empty before enrolment) and this headset's serial. False when the
        /// library refused the code (its reason arrives as an error).
        /// </summary>
        public bool Pair(string qr, string podId, string podName, string headsetId) =>
            headset.Call<bool>("pair", qr, podId, podName, headsetId);

        /// <summary>Keeps a session with the paired phone up, reconnecting when it drops.</summary>
        public void Connect() => headset.Call("connect");

        /// <summary>Stops looking for and talking to the phone; the pairing stays.</summary>
        public void Disconnect() => headset.Call("disconnect");

        /// <summary>Disconnects and deletes the pairing secret.</summary>
        public void Forget()
        {
            headset.Call("forget");
            Paired = false;
            PhoneName = "";
            OperatorName = "";
        }

        public bool SendSignInRequest(string userCode, string verificationUriComplete, long expiresAtMs) =>
            headset.Call<bool>("sendSignInRequest", userCode, verificationUriComplete, expiresAtMs);

        /// <summary>One of <paramref name="collector"/> (signed in) or <paramref name="error"/> is non-empty.</summary>
        public bool SendSignInResult(string collector, string error) => headset.Call<bool>("sendSignInResult", collector, error);

        /// <summary>
        /// Sends one HeadsetStatus, written by <see cref="HeadsetStatusMap"/>; the library repeats
        /// the latest every 5 s and sends it again when a session opens. False when none is open.
        /// </summary>
        public bool SendStatus(Action<IHeadsetStatusWriter> write)
        {
            using var builder = new AndroidJavaObject(StatusBuilderClassName);
            write(new BuilderWriter(builder));
            return headset.Call<bool>("sendStatus", builder);
        }

        public void Dispose()
        {
            try
            {
                headset.Call("close");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CompanionLink: close: {ex.Message}");
            }
            headset.Dispose();
        }

        private void RefreshPairing()
        {
            Paired = headset.Call<bool>("isPaired");
            PhoneName = Paired ? headset.Call<string>("pairedPhoneName") ?? "" : "";
            OperatorName = Paired ? headset.Call<string>("pairedOperatorName") ?? "" : "";
        }

        /// <summary>CompanionHeadset.Listener: each callback is queued for the main thread.</summary>
        private sealed class Listener : AndroidJavaProxy
        {
            private readonly ConcurrentQueue<(string, string, string)> events;

            public Listener(ConcurrentQueue<(string, string, string)> events) : base(ListenerInterfaceName) => this.events = events;

            // Called through JNI by name, on the library's thread.
            public void onState(string state, string detail) => events.Enqueue(("state", state ?? "", detail ?? ""));
            public void onPaired(string phoneName, string operatorDisplayName) => events.Enqueue(("paired", phoneName ?? "", operatorDisplayName ?? ""));
            public void onSignInAsk() => events.Enqueue(("sign_in", "", ""));
            public void onSignOutAsk() => events.Enqueue(("sign_out", "", ""));
            public void onError(string failure, string detail) => events.Enqueue(("error", failure ?? "", detail ?? ""));
        }

        /// <summary>HeadsetStatus fields into the library's HeadsetStatusBuilder; absent optionals go as -1.</summary>
        private sealed class BuilderWriter : IHeadsetStatusWriter
        {
            private readonly AndroidJavaObject builder;

            public BuilderWriter(AndroidJavaObject builder) => this.builder = builder;

            // Each builder call returns the builder; that extra reference is released at once.
            private void Call(string method, params object[] args) => builder.Call<AndroidJavaObject>(method, args)?.Dispose();

            public void Header(bool recording, int? batteryPct, string podId, string podName, string collector)
            {
                Call("setRecording", recording);
                Call("setBattery", batteryPct ?? -1);
                Call("setPod", podId, podName);
                Call("setCollector", collector);
            }

            public void Blocker(string headline, string action) => Call("setBlocker", headline, action);

            public void Device(string role, string side, string port, PodDeviceState state, string detail, string fix) =>
                Call("addDevice", role, side, port, HeadsetStatusMap.StateName(state), detail, fix);

            public void Collection(string task, long credited, long requested, long awaitingReview, string expects, bool factoryLinkOk, long? heardSecondsAgo) =>
                Call("setCollection", task, Clamp(credited), Clamp(requested), Clamp(awaitingReview), expects, factoryLinkOk,
                    heardSecondsAgo is long ago ? Clamp(ago) : -1);

            public void Episode(string id, int stage, string reason, string fix) =>
                Call("addEpisode", id, HeadsetStatusMap.StageNames[stage], reason, fix);

            private static int Clamp(long value) => (int)Math.Max(0, Math.Min(value, int.MaxValue));
        }
    }
}
