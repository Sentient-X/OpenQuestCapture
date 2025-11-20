# nullable enable

using UnityEngine;
using RealityLog.Common;
using OVR;

namespace RealityLog.UI
{
    /// <summary>
    /// Positions a world space canvas in front of the camera when the menu opens.
    /// The menu stays in place and doesn't follow the camera.
    /// </summary>
    public class WorldSpaceMenuPositioner : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Camera transform to position menu in front of (e.g., OVRCameraRig.CenterEyeAnchor)")]
        [SerializeField] private Transform cameraTransform = default!;

        [Header("Settings")]
        [Tooltip("Distance in front of camera (in meters)")]
        [SerializeField] private float distance = 2f;

        [Tooltip("Height offset from camera (in meters, positive = above eye level)")]
        [SerializeField] private float heightOffset = 0f;

        [Tooltip("Look at camera (rotate to face player)")]
        [SerializeField] private bool lookAtCamera = true;

        private Transform? canvasTransform;

        private void Start()
        {
            canvasTransform = transform;

            if (cameraTransform == null)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Camera transform not assigned!");
            }
        }

        /// <summary>
        /// Positions the canvas in front of the camera. Call this when the menu opens.
        /// </summary>
        public void PositionInFront()
        {
            if (cameraTransform == null || canvasTransform == null)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Camera transform or canvas transform is null!");
                return;
            }

            // Calculate position in front of camera
            Vector3 cameraPos = cameraTransform.position;
            Vector3 forward = cameraTransform.forward;
            Vector3 position = cameraPos + forward * distance;
            position.y += heightOffset;

            canvasTransform.position = position;

            // Rotate to face camera
            if (lookAtCamera)
            {
                Vector3 directionToCamera = cameraPos - canvasTransform.position;
                // Normalize before zeroing Y to maintain correct horizontal direction
                directionToCamera.Normalize();
                directionToCamera.y = 0; // Keep upright (only horizontal rotation)
                directionToCamera.Normalize(); // Renormalize after zeroing Y
                
                if (directionToCamera != Vector3.zero)
                {
                    // For UI canvas, forward should point toward camera (so user can see it)
                    // directionToCamera points from canvas to camera, so we use it directly
                    canvasTransform.rotation = Quaternion.LookRotation(-directionToCamera, Vector3.up);
                }
            }

            Vector3 canvasScale = canvasTransform.localScale;
            Quaternion canvasRotation = canvasTransform.rotation;
            
            Debug.Log($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Menu positioned at ({position.x:F2}, {position.y:F2}, {position.z:F2}) " +
                      $"in front of camera at ({cameraPos.x:F2}, {cameraPos.y:F2}, {cameraPos.z:F2}), " +
                      $"distance: {distance}m, height offset: {heightOffset}m");
            Debug.Log($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Canvas scale: ({canvasScale.x:F4}, {canvasScale.y:F4}, {canvasScale.z:F4}), " +
                      $"rotation: ({canvasRotation.eulerAngles.x:F1}°, {canvasRotation.eulerAngles.y:F1}°, {canvasRotation.eulerAngles.z:F1}°), " +
                      $"active: {canvasTransform.gameObject.activeSelf}, enabled: {canvasTransform.gameObject.activeInHierarchy}");
        }

        public void PositionAway()
        {
            if (canvasTransform == null)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Canvas transform is null!");
                return;
            }

            canvasTransform.position = new Vector3(1000.0f, 1000.0f, 1000.0f);

            Debug.Log($"[{Constants.LOG_TAG}] WorldSpaceMenuPositioner: Menu positioned away from camera");
        }
    }
}

