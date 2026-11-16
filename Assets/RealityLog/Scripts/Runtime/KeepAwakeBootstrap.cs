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
        /// <summary>
        /// Sets FLAG_KEEP_SCREEN_ON on Android's UI thread, which owns the window (Unity's main
        /// thread is not it: "CalledFromWrongThreadException ... Expected: main Calling: Thread-2"
        /// at every launch through build 75). Takes ownership of <paramref name="activity"/>.
        /// </summary>
        public static void KeepScreenOn(AndroidJavaObject activity)
        {
            activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
            {
                try
                {
                    using var window = activity.Call<AndroidJavaObject>("getWindow");
                    window.Call("addFlags", 128 /* FLAG_KEEP_SCREEN_ON */);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwake: Window flag failed: {ex.Message}");
                }
                finally
                {
                    activity.Dispose();
                }
            }));
        }

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

        // One partial wake lock for the process, however many pauses re-apply keep-awake.
        private static AndroidJavaObject? wakeLock;

        private void ApplyKeepAwake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // The headset's stay-on, screen-timeout and proximity settings are written over adb by
            // scripts/provision-headset.sh --keep-awake: an app may not write them. Through build 75
            // this ran Runtime.exec(String[]), which Unity's params expansion turned into
            // exec(String,String,String): NoSuchMethodError at every launch, so it never ran
            // (sx#4210, the on-device gate's logcat check).
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                if (wakeLock == null)
                {
                    using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                    wakeLock = powerManager.Call<AndroidJavaObject>("newWakeLock",
                        1 /* PARTIAL_WAKE_LOCK */, "RealityLog:BootKeepAwake");
                    wakeLock.Call("acquire");
                    Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake lock acquired");
                }
                KeepScreenOn(activity);
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
