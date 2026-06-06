using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Data-driven Hong Kong scene assembly. All parameters live in
/// Assets/HongKong/hk_setup.json — edit JSON, re-run menu items, no recompiles.
/// Menu: DroneSim/HK/*  (run top to bottom; each step is idempotent)
/// </summary>
public static class HKSceneBuilder
{
    const string ScenePath = "Assets/Scenes/HongKongScene.unity";
    const string FbxPath = "Assets/HongKong/Hong_Kong.fbx";
    const string ConfigPath = "Assets/HongKong/hk_setup.json";
    const string MatDir = "Assets/HongKong/Materials";

    // ---------- config DTOs ----------
    [Serializable] class Vec3 { public float x, y, z; public Vector3 V => new Vector3(x, y, z); }
    [Serializable] class CamDef { public string name; public Vec3 pos; public Vec3 lookAt; public float fov = 60f; public float far = 6000f; }
    [Serializable] class MatRule { public string contains; public string mat; }
    [Serializable] class CityCfg
    {
        public float scale = 1f;
        public Vec3 position = new Vec3();
        public float rotationY = 0f;
        public string[] deleteChildren = new string[0];
        public string[] keepOnlyChildren = new string[0];
    }
    [Serializable] class PathCfg { public Vec3[] waypoints = new Vec3[0]; public float clearanceRadius = 4f; }
    [Serializable] class LookCfg
    {
        public Vec3 sunEuler = new Vec3 { x = 35, y = -140, z = 0 };
        public float sunIntensity = 1.25f;
        public Vec3 sunColor = new Vec3 { x = 1f, y = 0.93f, z = 0.82f };
        public bool fog = true;
        public Vec3 fogColor = new Vec3 { x = 0.65f, y = 0.71f, z = 0.78f };
        public float fogDensity = 0.0012f;
        public float skyAtmosphere = 1.15f;
        public Vec3 skyTint = new Vec3 { x = 0.55f, y = 0.6f, z = 0.7f };
        public float bloom = 0.3f;
        public float postExposure = 0.2f;
        public float contrast = 12f;
        public float saturation = 6f;
        public float temperature = 6f;
        public float shadowDistance = 600f;
    }
    [Serializable] class Cfg
    {
        public CityCfg city = new CityCfg();
        public MatRule[] materialRules = new MatRule[0];
        public CamDef[] cameras = new CamDef[0];
        public PathCfg path = new PathCfg();
        public LookCfg look = new LookCfg();
        public CamDef spectator;
        public CamDef vista;
    }

    static Cfg Load()
    {
        if (!File.Exists(ConfigPath)) { Debug.LogError("[HK] missing " + ConfigPath); return new Cfg(); }
        return JsonUtility.FromJson<Cfg>(File.ReadAllText(ConfigPath));
    }

    static GameObject City() => GameObject.Find("HongKongCity");

    static void OpenScene()
    {
        if (SceneManager_ActiveScenePath() != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    static string SceneManager_ActiveScenePath() =>
        UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;

    static void Save()
    {
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    // ---------- 1. scene + model ----------
    [MenuItem("DroneSim/HK/1 Setup Scene")]
    public static void SetupScene()
    {
        if (!File.Exists(ScenePath))
        {
            if (!AssetDatabase.CopyAsset("Assets/Scenes/SimScene.unity", ScenePath))
            { Debug.LogError("[HK] scene copy failed"); return; }
        }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var env = GameObject.Find("Environment");
        if (env != null) UnityEngine.Object.DestroyImmediate(env);

        if (City() == null)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (model == null) { Debug.LogError("[HK] FBX missing at " + FbxPath); return; }
            var city = (GameObject)PrefabUtility.InstantiatePrefab(model);
            city.name = "HongKongCity";
        }

        // structure report
        var c = City().transform;
        var rends = City().GetComponentsInChildren<Renderer>();
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        var names = new List<string>();
        for (int i = 0; i < c.childCount; i++)
        {
            var ch = c.GetChild(i);
            var rb = ch.GetComponentsInChildren<Renderer>();
            Bounds cb = rb.Length > 0 ? rb[0].bounds : new Bounds(ch.position, Vector3.zero);
            foreach (var r in rb) cb.Encapsulate(r.bounds);
            names.Add($"{ch.name}[{rb.Length}r c={cb.center:F0} s={cb.size:F0}]");
        }
        Debug.Log($"[HK] SETUP: renderers={rends.Length} bounds.center={b.center:F0} bounds.size={b.size:F0} " +
                  $"rootScale={c.localScale:F3} children({c.childCount}): {string.Join(" | ", names)}");
        Save();
    }

    // ---------- 2. layout: scale/cut/layer/colliders ----------
    [MenuItem("DroneSim/HK/2 Apply Layout")]
    public static void ApplyLayout()
    {
        OpenScene();
        var cfg = Load().city;
        var city = City();
        if (city == null) { Debug.LogError("[HK] run Setup first"); return; }

        city.transform.localScale = Vector3.one * cfg.scale;
        city.transform.position = cfg.position.V;
        city.transform.rotation = Quaternion.Euler(0, cfg.rotationY, 0);

        int deleted = 0;
        var keep = new HashSet<string>(cfg.keepOnlyChildren);
        var del = new HashSet<string>(cfg.deleteChildren);
        for (int i = city.transform.childCount - 1; i >= 0; i--)
        {
            var ch = city.transform.GetChild(i);
            bool kill = del.Contains(ch.name) || (keep.Count > 0 && !keep.Contains(ch.name));
            if (kill) { UnityEngine.Object.DestroyImmediate(ch.gameObject); deleted++; }
        }

        int layer = LayerMask.NameToLayer("Buildings");
        int colliders = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = layer;
            mf.gameObject.isStatic = true;
            if (mf.GetComponent<MeshCollider>() == null && mf.sharedMesh != null)
            { mf.gameObject.AddComponent<MeshCollider>(); colliders++; }
        }

        var rends = city.GetComponentsInChildren<Renderer>();
        Bounds b = rends.Length > 0 ? rends[0].bounds : new Bounds();
        foreach (var r in rends) b.Encapsulate(r.bounds);
        Debug.Log($"[HK] LAYOUT: scale={cfg.scale} deleted={deleted} colliders+={colliders} " +
                  $"newBounds.center={b.center:F0} size={b.size:F0}");
        Save();
    }

    // ---------- 3. materials ----------
    [MenuItem("DroneSim/HK/3 Fix Materials")]
    public static void FixMaterials()
    {
        OpenScene();
        if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder("Assets/HongKong", "Materials");

        // bump -> normal map import settings
        var bumpImp = AssetImporter.GetAtPath("Assets/HongKong/Map_Texture/Hong_Kong_Bump.jpg") as TextureImporter;
        if (bumpImp != null && bumpImp.textureType != TextureImporterType.NormalMap)
        { bumpImp.textureType = TextureImporterType.NormalMap; bumpImp.SaveAndReimport(); }

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        Material Mk(string name, string colorTex, string bumpTex, float smooth)
        {
            string p = $"{MatDir}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(p);
            if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, p); }
            var col = AssetDatabase.LoadAssetAtPath<Texture2D>(colorTex);
            if (col != null) m.SetTexture("_BaseMap", col);
            if (!string.IsNullOrEmpty(bumpTex))
            {
                var bt = AssetDatabase.LoadAssetAtPath<Texture2D>(bumpTex);
                if (bt != null) { m.SetTexture("_BumpMap", bt); m.EnableKeyword("_NORMALMAP"); }
            }
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(m);
            return m;
        }

        var map = Mk("HK_Map", "Assets/HongKong/Map_Texture/Hong_Kong_Color.jpg", "Assets/HongKong/Map_Texture/Hong_Kong_Bump.jpg", 0.25f);
        var walls = new Material[7];
        for (int i = 1; i <= 6; i++)
            walls[i] = Mk($"HK_Wall{i}", $"Assets/HongKong/Building_Texture/Wall{i}_Color.png", null, 0.2f);

        var rules = Load().materialRules;
        int mapped = 0;
        var unmatched = new HashSet<string>();
        foreach (var r in City().GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int s = 0; s < mats.Length; s++)
            {
                string src = mats[s] != null ? mats[s].name : r.name;
                Material dst = null;
                for (int w = 1; w <= 6; w++)
                    if (src.IndexOf("Wall" + w, StringComparison.OrdinalIgnoreCase) >= 0) dst = walls[w];
                if (dst == null && (src.IndexOf("map", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    src.IndexOf("hong", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    src.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0)) dst = map;
                foreach (var rule in rules) // JSON overrides win
                    if (src.IndexOf(rule.contains, StringComparison.OrdinalIgnoreCase) >= 0)
                        dst = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{rule.mat}.mat");
                if (dst != null) { mats[s] = dst; mapped++; }
                else unmatched.Add(src);
            }
            r.sharedMaterials = mats;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[HK] MATERIALS: mapped={mapped} unmatched=[{string.Join(", ", unmatched)}]");
        Save();
    }

    // ---------- 4. cameras ----------
    [MenuItem("DroneSim/HK/4 Apply Cameras")]
    public static void ApplyCameras()
    {
        OpenScene();
        var cfg = Load();
        var rig = GameObject.Find("SurveillanceRig");
        if (rig == null) { Debug.LogError("[HK] no SurveillanceRig"); return; }
        int placed = 0;
        foreach (var cd in cfg.cameras)
        {
            var t = rig.transform.Find(cd.name);
            if (t == null) { Debug.LogWarning("[HK] no cam " + cd.name); continue; }
            t.position = cd.pos.V;
            t.LookAt(cd.lookAt.V);
            var cam = t.GetComponent<Camera>();
            cam.fieldOfView = cd.fov;
            cam.farClipPlane = cd.far; // HK scale: Peak/skyline sit 1-3km from the cams
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            placed++;
        }
        // poles from the block city don't apply here — remove them
        for (int i = rig.transform.childCount - 1; i >= 0; i--)
        {
            var ch = rig.transform.GetChild(i);
            if (ch.name.StartsWith("Pole_")) UnityEngine.Object.DestroyImmediate(ch.gameObject);
        }
        if (cfg.spectator != null && !string.IsNullOrEmpty(cfg.spectator.name))
        {
            var sp = GameObject.Find("SpectatorCamera");
            if (sp != null)
            {
                sp.transform.position = cfg.spectator.pos.V;
                sp.transform.LookAt(cfg.spectator.lookAt.V);
                sp.GetComponent<Camera>().GetUniversalAdditionalCameraData().renderPostProcessing = true;
            }
        }
        Debug.Log($"[HK] CAMERAS: placed={placed}/{cfg.cameras.Length}");
        Save();
    }

    // ---------- 5. flight path ----------
    [MenuItem("DroneSim/HK/5 Apply Path")]
    public static void ApplyPath()
    {
        OpenScene();
        var cfg = Load().path;
        var path = GameObject.Find("Path_0");
        if (path == null) { Debug.LogError("[HK] no Path_0"); return; }
        for (int i = path.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.DestroyImmediate(path.transform.GetChild(i).gameObject);
        for (int i = 0; i < cfg.waypoints.Length; i++)
        {
            var wp = new GameObject("wp" + i);
            wp.transform.SetParent(path.transform);
            wp.transform.position = cfg.waypoints[i].V;
        }
        var drone = GameObject.Find("Drone_0");
        if (drone != null && cfg.waypoints.Length > 0) drone.transform.position = cfg.waypoints[0].V;

        // clearance check (same Catmull-Rom as the runtime follower)
        Physics.SyncTransforms();
        int n = cfg.waypoints.Length;
        var hits = new List<string>();
        if (n >= 4)
        {
            int mask = LayerMask.GetMask("Buildings");
            for (int seg = 0; seg < n; seg++)
            {
                Vector3 p0 = cfg.waypoints[(seg - 1 + n) % n].V, p1 = cfg.waypoints[seg].V,
                        p2 = cfg.waypoints[(seg + 1) % n].V, p3 = cfg.waypoints[(seg + 2) % n].V;
                for (int s = 0; s < 24; s++)
                {
                    float t = s / 24f, t2 = t * t, t3 = t2 * t;
                    Vector3 pos = 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                    if (Physics.CheckSphere(pos, cfg.clearanceRadius, mask))
                        hits.Add($"seg{seg}@t{t:F2}={pos:F0}");
                }
            }
        }
        Debug.Log($"[HK] PATH: {n} waypoints, clearance hits={hits.Count}" +
                  (hits.Count > 0 ? " :: " + string.Join(" | ", hits.Take(8)) : ""));
        Save();
    }

    // ---------- 6. look (lighting/fog/sky/post) ----------
    [MenuItem("DroneSim/HK/6 Apply Look")]
    public static void ApplyLook()
    {
        OpenScene();
        var cfg = Load().look;

        foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            if (l.type == LightType.Directional)
            {
                l.transform.rotation = Quaternion.Euler(cfg.sunEuler.V);
                l.intensity = cfg.sunIntensity;
                l.color = new Color(cfg.sunColor.x, cfg.sunColor.y, cfg.sunColor.z);
                l.shadows = LightShadows.Soft;
            }

        RenderSettings.fog = cfg.fog;
        RenderSettings.fogMode = FogMode.Exponential;
        RenderSettings.fogColor = new Color(cfg.fogColor.x, cfg.fogColor.y, cfg.fogColor.z);
        RenderSettings.fogDensity = cfg.fogDensity;

        var sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/HongKong/Materials/HK_Sky.mat");
        if (sky == null)
        {
            sky = new Material(Shader.Find("Skybox/Procedural"));
            AssetDatabase.CreateAsset(sky, "Assets/HongKong/Materials/HK_Sky.mat");
        }
        sky.SetFloat("_AtmosphereThickness", cfg.skyAtmosphere);
        sky.SetColor("_SkyTint", new Color(cfg.skyTint.x, cfg.skyTint.y, cfg.skyTint.z));
        RenderSettings.skybox = sky;

        var rp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        if (rp != null) rp.shadowDistance = cfg.shadowDistance;
        var qrp = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (qrp != null) qrp.shadowDistance = cfg.shadowDistance;

        // global post volume
        var volGo = GameObject.Find("GlobalVolume");
        if (volGo == null) volGo = new GameObject("GlobalVolume");
        var vol = volGo.GetComponent<Volume>() ?? volGo.AddComponent<Volume>();
        vol.isGlobal = true;
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/HongKong/HK_Post.asset");
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, "Assets/HongKong/HK_Post.asset");
        }
        vol.sharedProfile = profile;

        T Comp<T>() where T : VolumeComponent
        {
            if (!profile.TryGet(out T c)) c = profile.Add<T>(true);
            return c;
        }
        var bloom = Comp<Bloom>(); bloom.intensity.Override(cfg.bloom); bloom.threshold.Override(0.9f);
        var tone = Comp<Tonemapping>(); tone.mode.Override(TonemappingMode.ACES);
        var ca = Comp<ColorAdjustments>();
        ca.postExposure.Override(cfg.postExposure);
        ca.contrast.Override(cfg.contrast);
        ca.saturation.Override(cfg.saturation);
        var wb = Comp<WhiteBalance>(); wb.temperature.Override(cfg.temperature);
        var vig = Comp<Vignette>(); vig.intensity.Override(0.18f);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        Debug.Log("[HK] LOOK applied (sun/fog/sky/post)");
        Save();
    }

    // ---------- utilities ----------
    /// Position the spectator camera from JSON "vista" for wide screenshots (far clip 40km, no post).
    [MenuItem("DroneSim/HK/0 Vista Cam")]
    public static void VistaCam()
    {
        OpenScene();
        var v = Load().vista;
        if (v == null) { Debug.LogError("[HK] no vista in JSON"); return; }
        var sp = GameObject.Find("SpectatorCamera");
        if (sp == null) { Debug.LogError("[HK] no SpectatorCamera"); return; }
        sp.transform.position = v.pos.V;
        sp.transform.LookAt(v.lookAt.V);
        var cam = sp.GetComponent<Camera>();
        cam.farClipPlane = 40000f;
        cam.fieldOfView = v.fov;
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
        Debug.Log($"[HK] VISTA @ {v.pos.V:F0} -> {v.lookAt.V:F0}");
    }

    /// Raycast a grid over the city, log the tallest clusters (finds the skyline numerically).
    [MenuItem("DroneSim/HK/0 Probe Heights")]
    public static void ProbeHeights()
    {
        OpenScene();
        Physics.SyncTransforms();
        int mask = LayerMask.GetMask("Buildings");
        var tops = new List<KeyValuePair<Vector3, float>>();
        float lo = float.MaxValue;
        for (float x = -7600f; x <= 7600f; x += 200f)
            for (float z = -4300f; z <= 4300f; z += 200f)
            {
                RaycastHit hit;
                if (Physics.Raycast(new Vector3(x, 1500f, z), Vector3.down, out hit, 3000f, mask))
                {
                    tops.Add(new KeyValuePair<Vector3, float>(hit.point, hit.point.y));
                    if (hit.point.y < lo) lo = hit.point.y;
                }
            }
        var best = tops.OrderByDescending(k => k.Value).Take(20)
                       .Select(k => $"({k.Key.x:F0},{k.Key.y:F0},{k.Key.z:F0})");
        Debug.Log($"[HK] PROBE: samples={tops.Count} minY={lo:F0} top20: {string.Join(" ", best)}");
    }

    // ---------- 7. build settings ----------
    [MenuItem("DroneSim/HK/7 Set As Build Scene")]
    public static void SetBuildScene()
    {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HK] build scene -> " + ScenePath);
    }
}
