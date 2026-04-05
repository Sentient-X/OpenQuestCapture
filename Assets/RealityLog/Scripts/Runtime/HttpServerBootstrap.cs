# nullable enable

using System;
using System.Collections;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    /// <summary>
    /// Bootstraps the HTTP server after a short delay so it doesn't block
    /// scene initialization and cause ANR on Quest.
    /// </summary>
    public class HttpServerBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Create a temporary MonoBehaviour to run a coroutine for delayed init
            var go = new GameObject("HttpServerBootstrap");
            go.AddComponent<HttpServerBootstrap>();
            DontDestroyOnLoad(go);
        }

        private IEnumerator Start()
        {
            // Wait for the app to fully initialize (5 seconds)
            yield return new WaitForSeconds(5f);

            try
            {
                var controllerType = Type.GetType("RealityLog.Network.HttpServerController, RealityLog.Network");
                if (controllerType == null)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerBootstrap: Could not find HttpServerController type");
                    yield break;
                }

                if (FindFirstObjectByType(controllerType) != null)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] HttpServerBootstrap: HttpServerController already exists");
                    yield break;
                }

                var serverGo = new GameObject("HttpServerController");
                serverGo.AddComponent(controllerType);
                DontDestroyOnLoad(serverGo);
                Debug.Log($"[{Constants.LOG_TAG}] HttpServerBootstrap: Created HttpServerController on port 8080");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] HttpServerBootstrap: Failed: {ex.Message}");
            }

            // Clean up bootstrap object
            Destroy(gameObject);
        }
    }
}
