# nullable enable

using System;
using System.Collections;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Network
{
    /// <summary>
    /// Bootstraps the CloudRelayService after the HTTP server is up.
    /// Creates a persistent GameObject with the service attached.
    /// Lives in RealityLog.Network assembly alongside CloudRelayService.
    /// </summary>
    public class CloudRelayBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("CloudRelayBootstrap");
            go.AddComponent<CloudRelayBootstrap>();
            DontDestroyOnLoad(go);
        }

        private IEnumerator Start()
        {
            // Wait for HTTP server and other systems to initialize (6 seconds)
            yield return new WaitForSeconds(6f);

            try
            {
                if (FindFirstObjectByType<CloudRelayService>() != null)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] CloudRelayBootstrap: CloudRelayService already exists");
                    yield break;
                }

                var relayGo = new GameObject("CloudRelayService");
                relayGo.AddComponent<CloudRelayService>();
                DontDestroyOnLoad(relayGo);
                Debug.Log($"[{Constants.LOG_TAG}] CloudRelayBootstrap: Created CloudRelayService");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CloudRelayBootstrap: Failed: {ex.Message}");
            }

            // Clean up bootstrap object
            Destroy(gameObject);
        }
    }
}
