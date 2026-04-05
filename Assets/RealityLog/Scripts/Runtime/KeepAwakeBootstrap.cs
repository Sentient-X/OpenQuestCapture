# nullable enable

using System;
using System.Collections;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    /// <summary>
    /// Immediately prevents the Quest from sleeping/freezing when the headset is removed.
    /// Runs at app startup with zero delay — before any other bootstrap.
    /// </summary>
    public class KeepAwakeBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Run immediately — no delay
            var go = new GameObject("KeepAwakeBootstrap");
            go.AddComponent<KeepAwakeBootstrap>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            // Prevent Unity from pausing when app loses focus (headset removal)
            Application.runInBackground = true;
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: runInBackground = true");

            ApplyKeepAwake();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: App pause event — re-applying keep-awake");
                // Re-apply on every pause event to ensure we stay awake
                ApplyKeepAwake();
            }
            else
            {
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: App resumed");
            }
        }

        private void ApplyKeepAwake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var runtime = new AndroidJavaClass("java.lang.Runtime")
                    .CallStatic<AndroidJavaObject>("getRuntime");

                // Disable proximity sensor — THE key fix for headset removal freeze
                using var p1 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/setprop", "debug.oculus.proximityDisabled", "1" });
                p1.Call<int>("waitFor");
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Proximity sensor DISABLED");

                // Max screen timeout
                using var p2 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "system", "screen_off_timeout", "2147483647" });
                p2.Call<int>("waitFor");

                // Stay on while plugged in (AC + USB + Wireless = 7)
                using var p3 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "global", "stay_on_while_plugged_in", "7" });
                p3.Call<int>("waitFor");

                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: All keep-awake settings applied");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Failed: {ex.Message}");
            }

            // Acquire wake lock
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                // FLAG_KEEP_SCREEN_ON = 128
                using var window = activity.Call<AndroidJavaObject>("getWindow");
                window.Call("addFlags", 128);

                // Partial wake lock
                using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                var wakeLock = powerManager.Call<AndroidJavaObject>("newWakeLock",
                    1 /* PARTIAL_WAKE_LOCK */, "RealityLog:BootKeepAwake");
                wakeLock.Call("acquire");

                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake lock acquired + screen on flag set");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake lock failed: {ex.Message}");
            }
#else
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Editor mode — skipping Android keep-awake");
#endif
        }
    }
}
