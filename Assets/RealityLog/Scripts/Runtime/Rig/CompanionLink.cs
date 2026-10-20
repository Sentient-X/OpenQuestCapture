# nullable enable

using System;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Rig
{
    /// <summary>
    /// The headset's side of the phone companion: the Java bridge
    /// (Assets/Plugins/Android/HeadsetCompanionBridge.java) over packages/sx-companion-android's
    /// GATT central, called through JNI from the main thread. The protocol (handshake, keys,
    /// framing, the pairing store) is the library's alone; this class only passes what the
    /// headset knows in and reads where the link stands. Null where the bridge is absent (the
    /// editor, a build without the library): the Pod card then says pairing is unavailable.
    /// </summary>
    public sealed class CompanionLink : IDisposable
    {
        public const string BridgeClassName = "io.sentientx.oqc.companion.HeadsetCompanionBridge";

        private readonly AndroidJavaObject bridge;

        private CompanionLink(AndroidJavaObject bridge) => this.bridge = bridge;

        /// <summary>The bridge on this activity, or null (with the reason logged) when it cannot load.</summary>
        public static CompanionLink? Open()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass(Constants.UNITY_PLAYER_CLASS_NAME);
                using var activity = player.GetStatic<AndroidJavaObject>(Constants.UNITY_PLAYER_CURRENT_ACTIVITY_VARIABLE_NAME);
                return new CompanionLink(new AndroidJavaObject(BridgeClassName, activity));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CompanionLink: the companion bridge did not load: {ex.Message}");
                return null;
            }
#else
            return null;
#endif
        }

        /// <summary>True when a phone is paired (its secret is in the store).</summary>
        public bool Paired => bridge.Call<bool>("paired");
        public string PhoneName => bridge.Call<string>("phoneName") ?? "";
        public string OperatorName => bridge.Call<string>("operatorName") ?? "";

        /// <summary>
        /// Where the link stands, from the bridge's closed set; a word this build does not know
        /// reads as a failure, never as connected.
        /// </summary>
        public CompanionPhase Phase => Parse(bridge.Call<string>("state") ?? "");

        /// <summary>The last failure's words, "" when none.</summary>
        public string Error => bridge.Call<string>("error") ?? "";

        public static CompanionPhase Parse(string state) => state switch
        {
            "idle" => CompanionPhase.NotPaired,
            "connecting" => CompanionPhase.Connecting,
            "confirming" => CompanionPhase.Confirming,
            "connected" => CompanionPhase.Connected,
            "reconnecting" => CompanionPhase.Reconnecting,
            "bluetooth_off" => CompanionPhase.BluetoothOff,
            _ => CompanionPhase.Failed,
        };

        /// <summary>
        /// Starts pairing with the phone that shows <paramref name="offer"/> (an sxc1: QR); the
        /// Hello names the pod (empty before enrolment) and this headset's serial.
        /// </summary>
        public void Pair(string offer, string podId, string podName, string headsetId) => bridge.Call("pair", offer, podId, podName, headsetId);

        /// <summary>Stops a pairing in progress; a paired phone stays paired.</summary>
        public void Cancel() => bridge.Call("cancel");

        /// <summary>Keeps reconnecting to the paired phone until <see cref="Forget"/> or <see cref="Dispose"/>.</summary>
        public void Resume() => bridge.Call("resume");

        /// <summary>Disconnects and deletes the pairing secret.</summary>
        public void Forget() => bridge.Call("forget");

        /// <summary>The next ask the phone sent: "sign_in", "sign_out", or "" when none waits.</summary>
        public string NextAsk() => bridge.Call<string>("nextAsk") ?? "";

        public void SendSignInRequest(string userCode, string verificationUriComplete, long expiresAtMs) =>
            bridge.Call("sendSignInRequest", userCode, verificationUriComplete, expiresAtMs);

        /// <summary>One of <paramref name="collector"/> (signed in) or <paramref name="error"/> is non-empty.</summary>
        public void SendSignInResult(string collector, string error) => bridge.Call("sendSignInResult", collector, error);

        /// <summary>Sends one HeadsetStatus, written by <see cref="HeadsetStatusMap"/>.</summary>
        public void SendStatus(Action<IHeadsetStatusWriter> write)
        {
            bridge.Call("statusBegin");
            write(new BridgeWriter(bridge));
            bridge.Call("statusSend");
        }

        public void Dispose()
        {
            try
            {
                bridge.Call("close");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] CompanionLink: close: {ex.Message}");
            }
            bridge.Dispose();
        }

        /// <summary>HeadsetStatus fields into the bridge's builder; absent optionals go as -1.</summary>
        private sealed class BridgeWriter : IHeadsetStatusWriter
        {
            private readonly AndroidJavaObject bridge;

            public BridgeWriter(AndroidJavaObject bridge) => this.bridge = bridge;

            public void Header(bool recording, int? batteryPct, string podId, string podName, string collector) =>
                bridge.Call("statusHeader", recording, batteryPct ?? -1, podId, podName, collector);

            public void Blocker(string headline, string action) => bridge.Call("statusBlocker", headline, action);

            public void Device(string role, string side, string port, PodDeviceState state, string detail, string fix) =>
                bridge.Call("statusDevice", role, side, port, HeadsetStatusMap.StateName(state), detail, fix);

            public void Collection(string task, long credited, long requested, long awaitingReview, string expects, bool factoryLinkOk, long? heardSecondsAgo) =>
                bridge.Call("statusCollection", task, (int)Math.Min(credited, int.MaxValue), (int)Math.Min(requested, int.MaxValue),
                    (int)Math.Min(awaitingReview, int.MaxValue), expects, factoryLinkOk, (int)Math.Min(heardSecondsAgo ?? -1, int.MaxValue));

            public void Episode(string id, int stage, string reason, string fix) =>
                bridge.Call("statusEpisode", id, HeadsetStatusMap.StageNames[stage], reason, fix);
        }
    }
}
