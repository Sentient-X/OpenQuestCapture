# nullable enable

using System;
using System.IO;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.Camera
{
    public class VideoRecorderSurfaceProvider : SurfaceProviderBase
    {
        private const string VIDEO_RECORDER_SURFACE_PROVIDER_CLASS_NAME = "com.samusynth.questcamera.io.VideoRecorderSurfaceProvider";
        private const string UPDATE_OUTPUT_FILE_METHOD_NAME = "updateOutputFile";
        private const string START_RECORDING_METHOD_NAME = "startRecording";
        private const string STOP_RECORDING_METHOD_NAME = "stopRecording";
        private const string CLOSE_METHOD_NAME = "close";

        [SerializeField] private string dataDirectoryName = string.Empty;
        [SerializeField] private string outputVideoFileName = "center_camera.mp4";
        [SerializeField] private string cameraMetaDataFileName = "center_camera_characteristics.json";
        [SerializeField] private int targetFrameRate = 30;
        [SerializeField] private int targetBitrateMbps = 8;
        [SerializeField] private int iFrameIntervalSeconds = 1;
        [SerializeField] private CameraSessionManager? cameraSessionManager = default!;

        private AndroidJavaObject? currentInstance;
        private CameraMetadata? cameraMetadata;

        public override AndroidJavaObject? GetJavaInstance(CameraMetadata metadata)
        {
            Close();

            cameraMetadata = metadata;
            cameraSessionManager ??= GetComponent<CameraSessionManager>();

            var size = metadata.sensor.pixelArraySize;
            var outputFilePath = BuildVideoOutputPath();

            try
            {
                currentInstance = new AndroidJavaObject(
                    VIDEO_RECORDER_SURFACE_PROVIDER_CLASS_NAME,
                    size.width,
                    size.height,
                    outputFilePath,
                    targetFrameRate,
                    targetBitrateMbps,
                    iFrameIntervalSeconds
                );

                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider initialized ({size.width}x{size.height}, {targetFrameRate}fps, {targetBitrateMbps}Mbps).");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                currentInstance = null;
            }

            return currentInstance;
        }

        public override void SetDataDirectoryName(string directoryName)
        {
            dataDirectoryName = directoryName;
        }

        public override void PrepareRecordingSession()
        {
            if (currentInstance == null)
            {
                return;
            }

            var outputFilePath = BuildVideoOutputPath();
            try
            {
                currentInstance.Call(UPDATE_OUTPUT_FILE_METHOD_NAME, outputFilePath);
                WriteCameraMetadataFile();
                cameraSessionManager?.ReopenSession();
                Debug.Log($"[{Constants.LOG_TAG}] VideoRecorderSurfaceProvider prepared output: {outputFilePath}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public override void StartRecordingSession()
        {
            if (currentInstance == null)
            {
                return;
            }

            try
            {
                currentInstance.Call(START_RECORDING_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        public override void StopRecordingSession()
        {
            if (currentInstance == null)
            {
                return;
            }

            try
            {
                currentInstance.Call(STOP_RECORDING_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private string BuildVideoOutputPath()
        {
            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
            Directory.CreateDirectory(dataDirPath);
            return Path.Join(dataDirPath, outputVideoFileName);
        }

        private void WriteCameraMetadataFile()
        {
            if (cameraMetadata == null)
            {
                return;
            }

            var dataDirPath = Path.Join(Application.persistentDataPath, dataDirectoryName);
            Directory.CreateDirectory(dataDirPath);

            var metadataPath = Path.Join(dataDirPath, cameraMetaDataFileName);
            var metadataJson = JsonUtility.ToJson(cameraMetadata);
            File.WriteAllText(metadataPath, metadataJson);
        }

        private void OnDestroy()
        {
            Close();
        }

        private void Close()
        {
            if (currentInstance == null)
            {
                return;
            }

            try
            {
                currentInstance.Call(CLOSE_METHOD_NAME);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                currentInstance.Dispose();
                currentInstance = null;
            }
        }
    }
}
