using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Bridge-independent automation: if Tools/hk_autorun.json exists at editor load,
/// run the listed HKSceneBuilder steps, capture verification screenshots to Captures/,
/// then rename the flag to .done so it runs exactly once.
/// Flag format: {"steps":["ImportHeroTextures","FixMaterials","ApplyLook","BuildHeroSet","ApplyCameras","ApplyPath","Screenshots"]}
/// </summary>
[InitializeOnLoad]
public static class HKAutoRun
{
    [Serializable] class Flag
    {
        public string[] steps = new string[0];
        public string scene = "";   // optional ScenePath override (photoreal: Assets/Scenes/HongKongPhotoScene.unity)
        public string config = "";  // optional ConfigPath override (photoreal: Assets/HongKong/hk_photo_setup.json)
    }

    static string FlagPath => Path.Combine(Directory.GetCurrentDirectory(), "Tools", "hk_autorun.json");

    static HKAutoRun()
    {
        EditorApplication.delayCall += () =>
        {
            if (!File.Exists(FlagPath)) return;
            EditorApplication.delayCall += Run; // one more frame so the editor settles
        };
    }

    static void Run()
    {
        Flag flag;
        try { flag = JsonUtility.FromJson<Flag>(File.ReadAllText(FlagPath)); }
        catch (Exception e) { Debug.LogError("[HK] AUTORUN flag parse failed: " + e.Message); return; }

        HKSceneBuilder.ScenePathOverride = flag.scene;
        HKSceneBuilder.ConfigPathOverride = flag.config;
        Debug.Log("[HK] AUTORUN start: " + string.Join(",", flag.steps)
                  + (string.IsNullOrEmpty(flag.scene) ? "" : $" scene={flag.scene}")
                  + (string.IsNullOrEmpty(flag.config) ? "" : $" config={flag.config}"));
        foreach (var step in flag.steps)
        {
            try
            {
                switch (step)
                {
                    case "SetupScene": HKSceneBuilder.SetupScene(); break;
                    case "ApplyLayout": HKSceneBuilder.ApplyLayout(); break;
                    case "ImportHeroTextures": HKSceneBuilder.ImportHeroTextures(); break;
                    case "FixMaterials": HKSceneBuilder.FixMaterials(); break;
                    case "ApplyLook": HKSceneBuilder.ApplyLook(); break;
                    case "BuildHeroSet": HKSceneBuilder.BuildHeroSet(); break;
                    case "ApplyCameras": HKSceneBuilder.ApplyCameras(); break;
                    case "ApplyPath": HKSceneBuilder.ApplyPath(); break;
                    case "SetBuildScene": HKSceneBuilder.SetBuildScene(); break;
                    case "ProbeZone": HKSceneBuilder.ProbeZone(); break;
                    case "ImportPhotoTiles": HKSceneBuilder.ImportPhotoTiles(); break;
                    case "TrimWater": HKSceneBuilder.TrimWater(); break;
                    case "TrimSpikes": HKSceneBuilder.TrimSpikes(); break;
                    case "ProbePhoto": HKSceneBuilder.ProbePhoto(); break;
                    case "ImportSurround": HKSceneBuilder.ImportSurround(); break;
                    case "Screenshots": Screenshots(); break;
                    case "SkyShot": SkyShot(); break;
                    default: Debug.LogWarning("[HK] AUTORUN unknown step " + step); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[HK] AUTORUN step {step} FAILED: {e.Message}\n{e.StackTrace}");
            }
        }
        try
        {
            string done = FlagPath + ".done";
            if (File.Exists(done)) File.Delete(done);
            File.Move(FlagPath, done);
        }
        catch { /* best effort */ }
        Debug.Log("[HK] AUTORUN COMPLETE");
    }

    [MenuItem("DroneSim/HK/9 Capture Cam Screenshots")]
    static void Screenshots()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Captures");
        Directory.CreateDirectory(dir);
        var rig = GameObject.Find("SurveillanceRig");
        if (rig != null)
            foreach (Camera c in rig.GetComponentsInChildren<Camera>(true))
                Capture(c, Path.Combine(dir, $"auto_{c.name}.png"));
        var spec = GameObject.Find("SpectatorCamera");
        if (spec != null) Capture(spec.GetComponent<Camera>(), Path.Combine(dir, "auto_spectator.png"));
        Debug.Log("[HK] AUTORUN screenshots -> Captures/auto_*.png");
    }

    /// <summary>Aerial verification frames: is the photoreal patch seated correctly in the surround?</summary>
    [MenuItem("DroneSim/HK/9 Capture Sky Shots")]
    static void SkyShot()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Captures");
        Directory.CreateDirectory(dir);
        var go = new GameObject("HKSkyShotCam");
        try
        {
            var cam = go.AddComponent<Camera>();
            cam.farClipPlane = 12000f;
            cam.fieldOfView = 70f;
            // straight down: patch (x±1258, z−1280..+490) + harbour + island shore (z≈−1413) all in frame
            go.transform.position = new Vector3(0f, 2800f, -600f);
            go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Capture(cam, Path.Combine(dir, "auto_sky_topdown.png"), 1280, 720);
            // oblique from the north: seam lines along the patch edges + island skyline bearing
            go.transform.position = new Vector3(0f, 1500f, 1500f);
            go.transform.LookAt(new Vector3(0f, 0f, -1200f));
            Capture(cam, Path.Combine(dir, "auto_sky_oblique.png"), 1280, 720);
            // low pass over the north seam: surround Kowloon meeting the photoreal tiles
            go.transform.position = new Vector3(-200f, 350f, 1100f);
            go.transform.LookAt(new Vector3(-200f, 0f, 300f));
            Capture(cam, Path.Combine(dir, "auto_sky_seam_north.png"), 1280, 720);
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
        Debug.Log("[HK] AUTORUN skyshot -> Captures/auto_sky_*.png");
    }

    static void Capture(Camera cam, string path, int W = 800, int H = 450)
    {
        var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.Create();
        var req = new RenderPipeline.StandardRequest { destination = rt };
        if (RenderPipeline.SupportsRenderRequest(cam, req))
        {
            RenderPipeline.SubmitRenderRequest(cam, req);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
        else Debug.LogWarning("[HK] render request unsupported for " + cam.name);
        rt.Release();
        UnityEngine.Object.DestroyImmediate(rt);
    }
}
