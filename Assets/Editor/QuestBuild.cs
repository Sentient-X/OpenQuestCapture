using UnityEditor;
using UnityEngine;

public class QuestBuild
{
    public static void Build()
    {
        string[] scenes = { "Assets/RealityLog/Scenes/RealityLogScene.unity" };
        string outputPath = "Builds/OpenQuestCapture.apk";
        
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
