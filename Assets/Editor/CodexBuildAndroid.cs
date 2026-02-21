#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;

public static class CodexBuildAndroid
{
    private const string XrTempDirectory = "Assets/XR/Temp";
    private const string XrResourcesDirectory = "Assets/XR/Resources";
    private static readonly string[] XrSimulationAssetNames =
    {
        "XRSimulationRuntimeSettings.asset",
        "XRSimulationPreferences.asset"
    };

    public static void BuildApk()
    {
        var outputPath = Environment.GetEnvironmentVariable("CODEX_BUILD_APK_PATH");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = "Builds/OpenQuestCapture.apk";
        }

        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDir = Path.GetDirectoryName(fullOutputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            throw new Exception("No enabled scenes found in EditorBuildSettings.");
        }

        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
        CleanupXrSimulationTempAssets();

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = fullOutputPath,
            target = BuildTarget.Android,
            options = BuildOptions.None
        };

        BuildReportSummary(BuildPipeline.BuildPlayer(options));
    }

    private static void CleanupXrSimulationTempAssets()
    {
        var removedAnyAssets = false;

        foreach (var assetName in XrSimulationAssetNames)
        {
            var tempAssetPath = $"{XrTempDirectory}/{assetName}";
            var resourcesAssetPath = $"{XrResourcesDirectory}/{assetName}";

            var tempAssetExists = AssetDatabase.LoadMainAssetAtPath(tempAssetPath) != null;
            var resourcesAssetExists = AssetDatabase.LoadMainAssetAtPath(resourcesAssetPath) != null;
            if (!tempAssetExists || !resourcesAssetExists)
            {
                continue;
            }

            if (AssetDatabase.DeleteAsset(tempAssetPath))
            {
                removedAnyAssets = true;
                Console.WriteLine($"[CodexBuildAndroid] Removed stale XR temp asset: {tempAssetPath}");
            }
            else
            {
                Console.WriteLine($"[CodexBuildAndroid] Failed to remove stale XR temp asset: {tempAssetPath}");
            }
        }

        if (!removedAnyAssets)
        {
            return;
        }

        if (AssetDatabase.IsValidFolder(XrTempDirectory))
        {
            var fullTempDirectory = Path.GetFullPath(XrTempDirectory);
            var hasNonMetaEntries =
                Directory.Exists(fullTempDirectory) &&
                Directory.EnumerateFileSystemEntries(fullTempDirectory)
                    .Any(entry => !entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase));

            if (!hasNonMetaEntries && AssetDatabase.DeleteAsset(XrTempDirectory))
            {
                Console.WriteLine($"[CodexBuildAndroid] Removed empty XR temp directory: {XrTempDirectory}");
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    private static void BuildReportSummary(UnityEditor.Build.Reporting.BuildReport report)
    {
        var summary = report.summary;
        Console.WriteLine($"[CodexBuildAndroid] Result: {summary.result}");
        Console.WriteLine($"[CodexBuildAndroid] Output: {summary.outputPath}");
        Console.WriteLine($"[CodexBuildAndroid] Duration: {summary.totalTime}");
        Console.WriteLine($"[CodexBuildAndroid] Size bytes: {summary.totalSize}");

        if (summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            throw new Exception($"Android build failed with result: {summary.result}");
        }
    }
}
#endif
