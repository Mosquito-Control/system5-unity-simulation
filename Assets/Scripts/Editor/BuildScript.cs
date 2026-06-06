using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line builds (bridge-independent):
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod BuildScript.BuildMac
///   Unity.exe -batchmode -quit -projectPath ... -executeMethod BuildScript.BuildWindows
/// </summary>
public static class BuildScript
{
    // Build whatever EditorBuildSettings says (DroneSim/HK/7 switches it); SimScene is the fallback.
    static string[] Scenes
    {
        get
        {
            var enabled = System.Array.FindAll(EditorBuildSettings.scenes, s => s.enabled);
            return enabled.Length > 0
                ? System.Array.ConvertAll(enabled, s => s.path)
                : new[] { "Assets/Scenes/SimScene.unity" };
        }
    }

    public static void BuildMac()
    {
        // Target machine is an Apple Silicon (M5) Mac — ARM64-only keeps it small.
        UnityEditor.OSXStandalone.UserBuildSettings.architecture = UnityEditor.Build.OSArchitecture.ARM64;
        Build(BuildTarget.StandaloneOSX, "Builds/macOS/DroneSim.app");
    }

    public static void BuildWindows()
    {
        Build(BuildTarget.StandaloneWindows64, "Builds/Windows/DroneSim.exe");
    }

    static void Build(BuildTarget target, string output)
    {
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = Scenes,
            locationPathName = output,
            target = target,
            options = BuildOptions.None
        });
        var s = report.summary;
        Debug.Log($"[Build] {s.platform} -> {s.result}, {s.totalSize / (1024 * 1024)} MB, " +
                  $"{s.totalErrors} errors, {s.totalWarnings} warnings, output: {output}");
        if (s.result != BuildResult.Succeeded)
            EditorApplication.Exit(1);
    }
}
