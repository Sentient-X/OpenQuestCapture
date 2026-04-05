# nullable enable

using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Core
{
    /// <summary>
    /// Manages the Android foreground service that prevents the OS from killing our process.
    /// Uses RuntimeInitializeOnLoadMethod to start as early as possible — before any scene
    /// MonoBehaviours run — so the process is protected even if the headset isn't being worn.
    /// </summary>
    public static class ForegroundServiceManager
    {
        private const string SERVICE_CLASS = "com.samusynth.questcamera.core.RecordingForegroundService";
        private static bool started = false;

        /// <summary>
        /// Auto-start the foreground service before any scene loads.
        /// This is the earliest safe point after the Unity engine and JNI are ready.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        private static void AutoStart()
        {
            StartService();
        }

        /// <summary>
        /// Start the foreground service. Safe to call multiple times — only starts once.
        /// Must be called from the main thread.
        /// </summary>
        public static void StartService()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (started) return;

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var serviceClass = new AndroidJavaClass(SERVICE_CLASS);
                serviceClass.CallStatic("start", activity);
                started = true;
                Debug.Log($"[{Constants.LOG_TAG}] ForegroundServiceManager: Service started — process protected");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] ForegroundServiceManager: Failed to start service: {ex.Message}");
            }
#else
            Debug.Log($"[{Constants.LOG_TAG}] ForegroundServiceManager: Skipped (not Android runtime)");
#endif
        }

        /// <summary>
        /// Stop the foreground service. Only call when the app is truly shutting down.
        /// </summary>
        public static void StopService()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!started) return;

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var serviceClass = new AndroidJavaClass(SERVICE_CLASS);
                serviceClass.CallStatic("stop", activity);
                started = false;
                Debug.Log($"[{Constants.LOG_TAG}] ForegroundServiceManager: Service stopped");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] ForegroundServiceManager: Failed to stop service: {ex.Message}");
            }
#endif
        }

        /// <summary>
        /// Update the notification text (e.g., "Recording in progress" or "Ready").
        /// </summary>
        public static void UpdateNotification(string text)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!started) return;

            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                using var serviceClass = new AndroidJavaClass(SERVICE_CLASS);
                serviceClass.CallStatic("updateNotification", activity, text);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] ForegroundServiceManager: Failed to update notification: {ex.Message}");
            }
#endif
        }
    }
}
