# nullable enable

using UnityEngine;
using RealityLog.Common;

namespace RealityLog.UI
{
    /// <summary>
    /// Sends the app to the Oculus Home when the left controller menu button (three lines) is pressed.
    /// Uses Android's moveTaskToBack to background the app, which brings up the Quest Home environment.
    /// Auto-bootstraps itself so no manual scene setup is needed.
    /// </summary>
    public class HomeButtonController : MonoBehaviour
    {
        [Header("Input Settings")]
        [Tooltip("Button to go home (Menu/Start button = three lines on left controller)")]
        [SerializeField] private OVRInput.Button homeButton = OVRInput.Button.Start;

        [Tooltip("Controller to check for button input (left controller has the menu button)")]
        [SerializeField] private OVRInput.Controller controller = OVRInput.Controller.LTouch;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("HomeButtonController");
            go.AddComponent<HomeButtonController>();
            DontDestroyOnLoad(go);
            Debug.Log($"[{Constants.LOG_TAG}] HomeButtonController: Bootstrapped - left menu button will go to Oculus Home");
        }

        private void Update()
        {
            if (OVRInput.GetDown(homeButton, controller))
            {
                GoToHome();
            }
        }

        /// <summary>
        /// Sends the app to the background, which brings up the Quest Home environment.
        /// </summary>
        public void GoToHome()
        {
            Debug.Log($"[{Constants.LOG_TAG}] HomeButtonController: Menu button pressed, going to Oculus Home");

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    activity.Call<bool>("moveTaskToBack", true);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] HomeButtonController: Failed to go to home: {e.Message}");
            }
#else
            Debug.Log($"[{Constants.LOG_TAG}] HomeButtonController: moveTaskToBack only works on Android/Quest device");
#endif
        }
    }
}
