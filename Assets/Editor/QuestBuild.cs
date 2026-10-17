using UnityEditor;
using UnityEngine;

public class QuestBuild
{
    public static void Build()
    {
        string[] scenes = { "Assets/RealityLog/Scenes/RealityLogScene.unity" };
        // OQC_BUILD_APK_PATH names the APK, so a build never overwrites another under Builds/.
        string outputPath = System.Environment.GetEnvironmentVariable("OQC_BUILD_APK_PATH");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = "Builds/OpenQuestCapture.apk";
        }
        
        // The on-headset seal's embodiment meshes ship as StreamingAssets, generated (not
        // committed) by scripts/build_embodiment_assets.sh. An APK without them could never
        // seal an episode, so the build refuses instead.
        const string embodimentManifest = "Assets/StreamingAssets/sx-embodiments/manifest.json";
        if (!System.IO.File.Exists(embodimentManifest))
        {
            Debug.LogError("Build refused: " + embodimentManifest + " is missing; run " +
                "SX_QUEST_RIG=<sx>/experience/data-factory/quest-rig scripts/build_embodiment_assets.sh");
            EditorApplication.Exit(1);
            return;
        }

        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        
        BuildPlayerOptions opts = new BuildPlayerOptions();
        opts.scenes = scenes;
        opts.locationPathName = outputPath;
        opts.target = BuildTarget.Android;
        opts.options = BuildOptions.None;
        
        var report = BuildPipeline.BuildPlayer(opts);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            Debug.LogError("Build failed: " + report.summary.result);
            EditorApplication.Exit(1);
        }
        else
        {
            Debug.Log("Build succeeded: " + outputPath);
            EditorApplication.Exit(0);
        }
    }
}
