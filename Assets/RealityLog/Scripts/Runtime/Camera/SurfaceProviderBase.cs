# nullable enable

using UnityEngine;

namespace RealityLog.Camera
{
    public interface ISurfaceProvider
    {
        AndroidJavaObject? GetJavaInstance(CameraMetadata metadata);
    }

    public abstract class SurfaceProviderBase : MonoBehaviour, ISurfaceProvider
    {
        public abstract AndroidJavaObject? GetJavaInstance(CameraMetadata metadata);

        public virtual void SetDataDirectoryName(string directoryName)
        {
        }

        public virtual void PrepareRecordingSession()
        {
        }

        public virtual void StartRecordingSession()
        {
        }

        /// <summary>
        /// Stops accepting new frames without waiting for encoder finalization.
        /// RecordingManager invokes this for every camera before finalizing either eye.
        /// </summary>
        public virtual void RequestStopRecordingSession()
        {
        }

        public virtual void StopRecordingSession()
        {
        }

        /// <summary>
        /// Main thread: begins this camera's stop and returns the part that blocks (an encoder
        /// drain, a file close), which RecordingManager runs off Unity's main thread with the JVM
        /// attached; null when nothing is left to do.
        /// </summary>
        public virtual System.Action? BeginStopRecordingSession()
        {
            StopRecordingSession();
            return null;
        }
    }
}
