# nullable enable

using System;
using System.IO;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog.FileOperations
{
    /// <summary>
    /// Handles file operations on recordings: delete, move to downloads, compress.
    /// </summary>
    public class RecordingOperations : MonoBehaviour
    {
        [Header("Settings")]
        [Tooltip("Base path for downloads folder on Android")]
        [SerializeField] private string downloadsBasePath = "/sdcard/Download";

        /// <summary>
        /// Event fired when an operation completes. Passes (operation, success, message).
        /// </summary>
        public event Action<string, bool, string>? OnOperationComplete;

        /// <summary>
        /// Deletes a recording directory and all its contents.
        /// </summary>
        public void DeleteRecording(string directoryName)
        {
            try
            {
                // The one delete rule (RecordingDeletePolicy): a session dir only, never
                // rig_staging, nothing whose rig episode is unsettled, and a sealed one only
                // with a confirmation the menu does not give (use the HTTP API's confirm).
                var (verdict, message, fullPath) =
                    RecordingDeletePolicy.Check(Application.persistentDataPath, directoryName, confirm: null);
                if (verdict != DeleteVerdict.Allowed || fullPath == null)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] RecordingOperations: refused to delete {directoryName}: {verdict}: {message}");
                    OnOperationComplete?.Invoke("Delete", false, message);
                    return;
                }

                Directory.Delete(fullPath, true);
                Debug.Log($"[{Constants.LOG_TAG}] RecordingOperations: Deleted recording {directoryName}");
                OnOperationComplete?.Invoke("Delete", true, $"Deleted {directoryName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingOperations: Error deleting {directoryName}: {e.Message}");
                OnOperationComplete?.Invoke("Delete", false, $"Error: {e.Message}");
            }
        }

        /// <summary>
        /// Moves a recording from app data to the Downloads folder.
        /// </summary>
        public void MoveToDownloads(string directoryName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageWrite))
            {
                // Request permission with callback
                var callbacks = new UnityEngine.Android.PermissionCallbacks();
                callbacks.PermissionGranted += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        ExecuteMoveToDownloads(directoryName);
                    }
                };
                callbacks.PermissionDenied += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        OnOperationComplete?.Invoke("MoveToDownloads", false, "Permission denied. Cannot move to Downloads.");
                    }
                };
                callbacks.PermissionDeniedAndDontAskAgain += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        OnOperationComplete?.Invoke("MoveToDownloads", false, "Permission denied permanently. Please enable in app settings.");
                    }
                };
                
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageWrite, callbacks);
                return;
            }
#endif
            // Permission already granted, execute move directly
            ExecuteMoveToDownloads(directoryName);
        }

        private void ExecuteMoveToDownloads(string directoryName)
        {
            try
            {
                string sourcePath = Path.Join(Application.persistentDataPath, directoryName);
                string destPath = Path.Join(downloadsBasePath, directoryName);

                if (!Directory.Exists(sourcePath))
                {
                    OnOperationComplete?.Invoke("MoveToDownloads", false, $"Directory not found: {directoryName}");
                    return;
                }

                // Create downloads directory if it doesn't exist
                if (!Directory.Exists(downloadsBasePath))
                {
                    Directory.CreateDirectory(downloadsBasePath);
                }

                // If destination exists, add timestamp suffix
                if (Directory.Exists(destPath))
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    destPath = Path.Join(downloadsBasePath, $"{directoryName}_{timestamp}");
                }

                // Move directory
                Directory.Move(sourcePath, destPath);
                
                // Trigger media scan so file appears in Quest Files app immediately
                ScanDirectory(destPath);
                
                Debug.Log($"[{Constants.LOG_TAG}] RecordingOperations: Moved {directoryName} to Downloads");
                OnOperationComplete?.Invoke("MoveToDownloads", true, $"Moved to Downloads: {Path.GetFileName(destPath)}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] RecordingOperations: Error moving {directoryName}: {e.Message}");
                OnOperationComplete?.Invoke("MoveToDownloads", false, $"Error: {e.Message}");
            }
        }

        /// <summary>
        /// Event fired when an operation reports progress. Passes (operation, progress 0-1).
        /// </summary>
        public event Action<string, float>? OnOperationProgress;

        /// <summary>
        /// Compresses a recording directory into a ZIP file asynchronously.
        /// </summary>
        public void CompressRecordingAsync(string directoryName)
        {
            StartCoroutine(CompressCoroutine(directoryName, false));
        }

        /// <summary>
        /// Exports a recording by compressing it and moving the ZIP to Downloads asynchronously.
        /// </summary>
        public void ExportRecordingAsync(string directoryName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.ExternalStorageWrite))
            {
                // Request permission with callback
                var callbacks = new UnityEngine.Android.PermissionCallbacks();
                callbacks.PermissionGranted += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        StartCoroutine(CompressCoroutine(directoryName, true));
                    }
                };
                callbacks.PermissionDenied += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        OnOperationComplete?.Invoke("Export", false, "Permission denied. Cannot export to Downloads.");
                    }
                };
                callbacks.PermissionDeniedAndDontAskAgain += (string permission) =>
                {
                    if (permission == UnityEngine.Android.Permission.ExternalStorageWrite)
                    {
                        OnOperationComplete?.Invoke("Export", false, "Permission denied permanently. Please enable in app settings.");
                    }
                };
                
                UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.ExternalStorageWrite, callbacks);
                return;
            }
#endif
            // Permission already granted, execute export directly
            StartCoroutine(CompressCoroutine(directoryName, true));
        }

        private System.Collections.IEnumerator CompressCoroutine(string directoryName, bool isExport)
        {
            string operationName = isExport ? "Export" : "Compress";
            string sourcePath = Path.Join(Application.persistentDataPath, directoryName);
            string zipName = $"{directoryName}.zip";
            string zipPath = Path.Join(Application.persistentDataPath, zipName);

            // Enable runInBackground to ensure operation continues if headset is removed
            bool originalRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            try
            {
                if (!Directory.Exists(sourcePath))
                {
                    OnOperationComplete?.Invoke(operationName, false, $"Directory not found: {directoryName}");
                    yield break;
                }

                // Delete existing zip if it exists
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                }

                // Use ZipHelper for background compression
                System.Threading.Tasks.Task compTask;
                ZipHelper.CompressionProgress progressState;
                try
                {
                    (compTask, progressState) = ZipHelper.CompressDirectoryAsync(sourcePath, zipPath);
                }
                catch (Exception e)
                {
                    OnOperationComplete?.Invoke(operationName, false, $"Error listing files: {e.Message}");
                    yield break;
                }

                // Poll for progress on main thread
                while (!progressState.IsDone)
                {
                    float progress = progressState.TotalFiles > 0
                        ? (float)progressState.ProcessedFiles / progressState.TotalFiles
                        : 0f;
                    OnOperationProgress?.Invoke(operationName, progress);
                    yield return null;
                }

                // Final progress update
                OnOperationProgress?.Invoke(operationName, 1.0f);

                if (progressState.Exception != null)
                {
                    OnOperationComplete?.Invoke(operationName, false, $"Error: {progressState.Exception.Message}");
                    yield break;
                }

                if (isExport)
                {
                    try
                    {
                        // Create downloads directory if it doesn't exist
                        if (!Directory.Exists(downloadsBasePath))
                        {
                            Directory.CreateDirectory(downloadsBasePath);
                        }

                        string destZipPath = Path.Join(downloadsBasePath, zipName);

                        // If destination zip exists, add timestamp suffix
                        if (File.Exists(destZipPath))
                        {
                            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                            string zipNameWithoutExt = Path.GetFileNameWithoutExtension(zipName);
                            destZipPath = Path.Join(downloadsBasePath, $"{zipNameWithoutExt}_{timestamp}.zip");
                        }

                        // Move zip to downloads
                        File.Move(zipPath, destZipPath);

                        // Trigger media scan so file appears in Quest Files app immediately
                        ScanFile(destZipPath);

                        Debug.Log($"[{Constants.LOG_TAG}] RecordingOperations: Exported {directoryName} to {destZipPath}");
                        OnOperationComplete?.Invoke("Export", true, $"Exported to Downloads: {Path.GetFileName(destZipPath)}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[{Constants.LOG_TAG}] RecordingOperations: Error exporting {directoryName}: {e.Message}");
                        OnOperationComplete?.Invoke("Export", false, $"Error: {e.Message}");
                    }
                }
                else
                {
                    Debug.Log($"[{Constants.LOG_TAG}] RecordingOperations: Compressed {directoryName} to {zipPath}");
                    OnOperationComplete?.Invoke("Compress", true, $"Compressed to {directoryName}.zip");
                }
            }
            finally
            {
                // Restore original runInBackground setting
                Application.runInBackground = originalRunInBackground;
            }
        }

        // Keeping synchronous methods for compatibility if needed, or we can remove them.
        // The user asked to "compress and export in a coroutine", implying replacement or addition.
        // I will remove the old synchronous bodies to avoid confusion, or redirect them.
        // For now, I have replaced them in the file content range.

        /// <summary>
        /// Compresses a recording directory into a ZIP file.
        /// </summary>
        public void CompressRecording(string directoryName)
        {
           CompressRecordingAsync(directoryName);
        }

        /// <summary>
        /// Exports a recording by compressing it and moving the ZIP to Downloads.
        /// </summary>
        public void ExportRecording(string directoryName)
        {
            ExportRecordingAsync(directoryName);
        }

        /// <summary>
        /// Gets the Downloads folder path for the current platform.
        /// </summary>
        public string GetDownloadsPath()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return downloadsBasePath;
#else
            // For editor/testing, use a local downloads folder
            return Path.Join(Application.persistentDataPath, "Downloads");
#endif
        }

        /// <summary>
        /// Notify Android's MediaStore about a new file so it appears immediately in file browsers.
        /// </summary>
        private void ScanFile(string filePath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject context = currentActivity.Call<AndroidJavaObject>("getApplicationContext"))
                using (AndroidJavaClass mediaScanner = new AndroidJavaClass("android.media.MediaScannerConnection"))
                {
                    mediaScanner.CallStatic("scanFile", context, new string[] { filePath }, null, null);
                }
                
                Debug.Log($"[{Constants.LOG_TAG}] Triggered media scan for: {filePath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Failed to trigger media scan: {e.Message}");
            }
#endif
        }

        /// <summary>
        /// Notify Android's MediaStore about all files in a directory.
        /// </summary>
        private void ScanDirectory(string directoryPath)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                // Get all files in directory recursively
                string[] files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
                
                if (files.Length == 0)
                    return;

                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject context = currentActivity.Call<AndroidJavaObject>("getApplicationContext"))
                using (AndroidJavaClass mediaScanner = new AndroidJavaClass("android.media.MediaScannerConnection"))
                {
                    mediaScanner.CallStatic("scanFile", context, files, null, null);
                }
                
                Debug.Log($"[{Constants.LOG_TAG}] Triggered media scan for {files.Length} files in: {directoryPath}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] Failed to trigger media scan for directory: {e.Message}");
            }
#endif
        }
    }
}

