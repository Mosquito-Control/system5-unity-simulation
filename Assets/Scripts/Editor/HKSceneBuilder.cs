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
    // Defaults follow the ACTIVE scene (photo scene open -> photo config), so menu items
    // are safe to click directly; HKAutoRun can still override per flag run. Overrides are
    // statics and reset on every domain reload — never rely on them surviving a recompile.
    public static string ScenePathOverride, ConfigPathOverride;
    static bool PhotoSceneActive =>
        UnityEngine.SceneManagement.SceneManager.GetActiveScene().path.Contains("HongKongPhotoScene");
    static string ScenePath => !string.IsNullOrEmpty(ScenePathOverride) ? ScenePathOverride
        : (PhotoSceneActive ? "Assets/Scenes/HongKongPhotoScene.unity" : "Assets/Scenes/HongKongScene.unity");
    static string ConfigPath => !string.IsNullOrEmpty(ConfigPathOverride) ? ConfigPathOverride
        : (PhotoSceneActive ? "Assets/HongKong/hk_photo_setup.json" : "Assets/HongKong/hk_setup.json");
    const string FbxPath = "Assets/HongKong/Hong_Kong.fbx";
    const string MatDir = "Assets/HongKong/Materials";

    // ---------- config DTOs ----------
    [Serializable] class Vec3 { public float x, y, z; public Vector3 V => new Vector3(x, y, z); }
    [Serializable] class CamDef
    {
        public string name; public Vec3 pos; public Vec3 lookAt;
        public float fov = 60f; public float far = 6000f;
        public string mount = "none"; // pole | wall | roof | none
    }
    [Serializable] class MatRule { public string contains; public string mat; }
    [Serializable] class CityCfg
    {
        public float scale = 1f;
        public Vec3 position = new Vec3();
        public float rotationY = 0f;
        public string[] deleteChildren = new string[0];
        public string[] keepOnlyChildren = new string[0];
    }
    [Serializable] class PathCfg
    {
        public Vec3[] waypoints = new Vec3[0];
        public float clearanceRadius = 4f;
        public float startOffset = 0f;
        public Vec3 droneTint;
    }
    [Serializable] class RcCfg
    {
        public bool enabled = false;
        public Vec3 spawn = new Vec3();
        public float yawDeg = 0f;            // 0 = facing north (+Z)
        public float maxHorizSpeed = 12f, maxClimbRate = 5f, maxYawRate = 90f, accel = 20f;
        public float minAltitude = 5f, maxAltitude = 260f, deadzone = 0.05f;
        public int rollCh = 0, pitchCh = 1, thrCh = 2, yawCh = 3;
        public bool invertRoll = false, invertPitch = true, invertThr = false, invertYaw = false;
        public bool throttleCentered = true;
    }
    [Serializable] class WaterCfg
    {
        public bool enabled = false;
        public Vec3 center = new Vec3 { x = 1000, y = 0.8f, z = 2050 };
        public Vec3 size = new Vec3 { x = 3600, y = 1, z = 1400 };
        public Vec3 color = new Vec3 { x = 0.08f, y = 0.12f, z = 0.16f };
        public float smoothness = 0.92f;
    }
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
        public Vec3 skyGround = new Vec3 { x = 0.36f, y = 0.39f, z = 0.43f }; // below-horizon hemisphere
        public string skyPanorama = "";        // HDRI texture asset path; non-empty switches to panoramic skybox
        public float skyPanoExposure = 1.0f;
        public float skyPanoRotation = 0f;
        public float vignette = 0.18f;
        public float grain = 0f;               // FilmGrain intensity (sensor noise)
        public float grainResponse = 0.6f;     // lower = noise survives into mids/highlights like a real sensor
        public float chromAb = 0f;             // ChromaticAberration intensity (lens edges)
        public float tint = 0f;                // WhiteBalance green(-)/magenta(+) — video feeds drift slightly green
        public float blackLift = 0f;           // LiftGammaGain lift.w: cheap sensors never reach true black
        public float gamma = 0f;               // LiftGammaGain gamma.w: midtone bias
        public float lensDistort = 0f;         // barrel(+) distortion of wide surveillance lenses
        public float bloom = 0.3f;
        public float postExposure = 0.2f;
        public float contrast = 12f;
        public float saturation = 6f;
        public float temperature = 6f;
        public float shadowDistance = 600f;
        public float emissionIntensity = 0f; // 0 = windows off
        public Vec3 emissionTint = new Vec3 { x = 1f, y = 0.72f, z = 0.42f };
        public WaterCfg water = new WaterCfg();
    }
    [Serializable] class HeroDef
    {
        public float x, z;          // footprint center (ground found by raycast)
        public float w = 30, d = 30, h = 60;
        public int mat = 0;         // index into HeroTextures facade sets
        public bool setback = false;
        public float rotY = 0f;
    }
    [Serializable] class Cfg
    {
        public CityCfg city = new CityCfg();
        public MatRule[] materialRules = new MatRule[0];
        public CamDef[] cameras = new CamDef[0];
        public PathCfg[] paths = new PathCfg[0];
        public HeroDef[] heroes = new HeroDef[0];
        public LookCfg look = new LookCfg();
        public CamDef spectator;
        public CamDef vista;
        public RcCfg rc;
    }

    static Cfg Load()
    {
        if (!File.Exists(ConfigPath)) { Debug.LogError("[HK] missing " + ConfigPath); return new Cfg(); }
        return JsonUtility.FromJson<Cfg>(File.ReadAllText(ConfigPath));
    }

    static GameObject City() => GameObject.Find("HongKongCity");

    static void OpenScene()
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().path != ScenePath)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
    }

    static void Save()
    {
        EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
    }

    static Material LoadOrCreateMat(string name, Action<Material> init = null)
    {
        string p = $"{MatDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(p);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            init?.Invoke(m);
            AssetDatabase.CreateAsset(m, p);
        }
        return m;
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
        var rends = City().GetComponentsInChildren<Renderer>();
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        Debug.Log($"[HK] SETUP: renderers={rends.Length} bounds.center={b.center:F0} bounds.size={b.size:F0}");
        Save();
    }

    // ---------- 2. layout ----------
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
        Debug.Log($"[HK] LAYOUT: scale={cfg.scale} deleted={deleted} colliders+={colliders}");
        Save();
    }

    // ---------- 3. materials (incl. window emission) ----------
    [MenuItem("DroneSim/HK/3 Fix Materials")]
    public static void FixMaterials()
    {
        OpenScene();
        if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder("Assets/HongKong", "Materials");
        var look = Load().look;

        var bumpImp = AssetImporter.GetAtPath("Assets/HongKong/Map_Texture/Hong_Kong_Bump.jpg") as TextureImporter;
        if (bumpImp != null && bumpImp.textureType != TextureImporterType.NormalMap)
        { bumpImp.textureType = TextureImporterType.NormalMap; bumpImp.SaveAndReimport(); }

        Material Mk(string name, string colorTex, string bumpTex, float smooth, string emissionTex)
        {
            var m = LoadOrCreateMat(name);
            var col = AssetDatabase.LoadAssetAtPath<Texture2D>(colorTex);
            if (col != null) m.SetTexture("_BaseMap", col);
            if (!string.IsNullOrEmpty(bumpTex))
            {
                var bt = AssetDatabase.LoadAssetAtPath<Texture2D>(bumpTex);
                if (bt != null) { m.SetTexture("_BumpMap", bt); m.EnableKeyword("_NORMALMAP"); }
            }
            m.SetFloat("_Smoothness", smooth);
            m.SetFloat("_Metallic", 0f);
            if (!string.IsNullOrEmpty(emissionTex) && look.emissionIntensity > 0f)
            {
                var et = AssetDatabase.LoadAssetAtPath<Texture2D>(emissionTex);
                if (et != null)
                {
                    m.EnableKeyword("_EMISSION");
                    m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    m.SetTexture("_EmissionMap", et);
                    var t = look.emissionTint.V * look.emissionIntensity;
                    m.SetColor("_EmissionColor", new Color(t.x, t.y, t.z));
                }
            }
            else
            {
                m.DisableKeyword("_EMISSION");
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        var map = Mk("HK_Map", "Assets/HongKong/Map_Texture/Hong_Kong_Color.jpg",
                     "Assets/HongKong/Map_Texture/Hong_Kong_Bump.jpg", 0.3f, null);
        var walls = new Material[7];
        for (int i = 1; i <= 6; i++)
            walls[i] = Mk($"HK_Wall{i}", $"Assets/HongKong/Building_Texture/Wall{i}_Color.png",
                          null, 0.25f, $"Assets/HongKong/Building_Texture/Wall{i}_Emission.png");

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
                foreach (var rule in rules)
                    if (src.IndexOf(rule.contains, StringComparison.OrdinalIgnoreCase) >= 0)
                        dst = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/{rule.mat}.mat");
                if (dst != null) { mats[s] = dst; mapped++; }
                else unmatched.Add(src);
            }
            r.sharedMaterials = mats;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[HK] MATERIALS: mapped={mapped} emission={(look.emissionIntensity > 0 ? "ON" : "off")} unmatched=[{string.Join(", ", unmatched)}]");
        Save();
    }

    // ---------- 4. cameras + CCTV props ----------
    [MenuItem("DroneSim/HK/4 Apply Cameras")]
    public static void ApplyCameras()
    {
        OpenScene();
        var cfg = Load();
        var rig = GameObject.Find("SurveillanceRig");
        if (rig == null) rig = new GameObject("SurveillanceRig");

        // wipe old props (regenerated below) and cameras dropped from the config
        var keep = new HashSet<string>(cfg.cameras.Select(c => c.name));
        for (int i = rig.transform.childCount - 1; i >= 0; i--)
        {
            var ch = rig.transform.GetChild(i);
            if (ch.name.StartsWith("Pole_") || ch.name.StartsWith("Prop_") || !keep.Contains(ch.name))
                UnityEngine.Object.DestroyImmediate(ch.gameObject);
        }

        var housingMat = LoadOrCreateMat("HK_CamHousing", m => m.color = new Color(0.92f, 0.92f, 0.9f));
        var steelMat = LoadOrCreateMat("HK_CamSteel", m => m.color = new Color(0.35f, 0.36f, 0.38f));

        int placed = 0;
        foreach (var cd in cfg.cameras)
        {
            var t = rig.transform.Find(cd.name);
            if (t == null)
            {
                var go = new GameObject(cd.name);
                go.transform.SetParent(rig.transform);
                go.AddComponent<Camera>().enabled = false;
                t = go.transform;
            }
            Vector3 cp = cd.pos.V;
            if (cd.mount == "pole")
            {
                // pole cams: pos.y is height ABOVE LOCAL GROUND. Photogrammetry terrain
                // varies 2-15 m across TST; absolute heights buried lampposts underground.
                Physics.SyncTransforms();
                RaycastHit gh;
                if (Physics.Raycast(new Vector3(cp.x, 400f, cp.z), Vector3.down, out gh, 800f,
                                    LayerMask.GetMask("Buildings")))
                {
                    if (gh.point.y > 30f)
                        Debug.LogWarning($"[HK] {cd.name}: pole ground at {gh.point.y:F0} m — position is on a building footprint?");
                    cp.y = gh.point.y + cd.pos.V.y;
                }
            }
            t.position = cp;
            t.LookAt(cd.lookAt.V);
            var cam = t.GetComponent<Camera>();
            cam.fieldOfView = cd.fov;
            cam.farClipPlane = cd.far;
            cam.nearClipPlane = 0.35f; // housing sits inside near plane, invisible to itself
            cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            BuildProp(rig.transform, cd, t);
            placed++;
        }
        if (cfg.spectator != null && !string.IsNullOrEmpty(cfg.spectator.name))
        {
            var sp = GameObject.Find("SpectatorCamera");
            if (sp != null)
            {
                sp.transform.position = cfg.spectator.pos.V;
                sp.transform.LookAt(cfg.spectator.lookAt.V);
                var sc = sp.GetComponent<Camera>();
                sc.fieldOfView = cfg.spectator.fov;
                sc.farClipPlane = cfg.spectator.far;
                sc.GetUniversalAdditionalCameraData().renderPostProcessing = true;
            }
        }
        Debug.Log($"[HK] CAMERAS: placed={placed}/{cfg.cameras.Length} (props rebuilt)");
        Save();
    }

    static void BuildProp(Transform rig, CamDef cd, Transform camT)
    {
        var root = new GameObject("Prop_" + cd.name);
        root.transform.SetParent(rig);
        root.transform.position = camT.position;

        // housing box just behind the lens (inside own near plane, visible to other cams)
        var housing = GameObject.CreatePrimitive(PrimitiveType.Cube);
        housing.name = "housing";
        housing.transform.SetParent(root.transform, false);
        housing.transform.localScale = new Vector3(0.28f, 0.3f, 0.55f);
        housing.transform.position = camT.position - camT.forward * 0.28f;
        housing.transform.rotation = camT.rotation;
        UnityEngine.Object.DestroyImmediate(housing.GetComponent<Collider>());
        housing.GetComponent<MeshRenderer>().sharedMaterial =
            AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/HK_CamHousing.mat");

        var steel = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/HK_CamSteel.mat");
        if (cd.mount == "pole")
        {
            // pole from camera down to whatever surface is below
            float groundY = 0f;
            RaycastHit hit;
            if (Physics.Raycast(camT.position + Vector3.up * 0.5f, Vector3.down, out hit, 500f,
                                LayerMask.GetMask("Buildings")))
                groundY = hit.point.y;
            float len = Mathf.Max(1f, camT.position.y - groundY);
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "pole";
            pole.transform.SetParent(root.transform, false);
            pole.transform.position = new Vector3(camT.position.x, groundY + len * 0.5f, camT.position.z)
                                      - camT.forward * 0.35f;
            pole.transform.localScale = new Vector3(0.22f, len * 0.5f, 0.22f);
            UnityEngine.Object.DestroyImmediate(pole.GetComponent<Collider>());
            pole.GetComponent<MeshRenderer>().sharedMaterial = steel;
            // arm from pole top to housing
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = "arm";
            arm.transform.SetParent(root.transform, false);
            arm.transform.position = camT.position - camT.forward * 0.32f + Vector3.up * 0.18f;
            arm.transform.rotation = camT.rotation;
            arm.transform.localScale = new Vector3(0.08f, 0.08f, 0.5f);
            UnityEngine.Object.DestroyImmediate(arm.GetComponent<Collider>());
            arm.GetComponent<MeshRenderer>().sharedMaterial = steel;
        }
        else if (cd.mount == "wall" || cd.mount == "roof")
        {
            // bracket arm extending behind the housing toward the mounting surface
            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = "bracket";
            arm.transform.SetParent(root.transform, false);
            float reach = cd.mount == "wall" ? 1.0f : 0.0f;
            Vector3 back = cd.mount == "wall" ? -camT.forward : Vector3.down;
            arm.transform.position = camT.position + back * (0.45f + reach * 0.5f);
            arm.transform.rotation = cd.mount == "wall" ? camT.rotation : Quaternion.identity;
            arm.transform.localScale = cd.mount == "wall"
                ? new Vector3(0.1f, 0.1f, 0.4f + reach)
                : new Vector3(0.15f, 0.9f, 0.15f);
            UnityEngine.Object.DestroyImmediate(arm.GetComponent<Collider>());
            arm.GetComponent<MeshRenderer>().sharedMaterial = steel;
        }
    }

    // ---------- 5. flight paths (multi-drone) ----------
    [MenuItem("DroneSim/HK/5 Apply Path")]
    public static void ApplyPath()
    {
        OpenScene();
        var cfg = Load();
        var fp = GameObject.Find("FlightPaths");
        if (fp == null) fp = new GameObject("FlightPaths");
        var dronesRoot = GameObject.Find("Drones");
        if (dronesRoot == null) dronesRoot = new GameObject("Drones");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Prefabs/Drone.prefab");

        Physics.SyncTransforms();
        int mask = LayerMask.GetMask("Buildings");
        var report = new List<string>();

        for (int d = 0; d < cfg.paths.Length; d++)
        {
            var pc = cfg.paths[d];
            // path object
            var pathT = fp.transform.Find("Path_" + d);
            if (pathT == null)
            {
                var pgo = new GameObject("Path_" + d);
                pgo.transform.SetParent(fp.transform);
                pathT = pgo.transform;
            }
            for (int i = pathT.childCount - 1; i >= 0; i--)
                UnityEngine.Object.DestroyImmediate(pathT.GetChild(i).gameObject);
            for (int i = 0; i < pc.waypoints.Length; i++)
            {
                var wp = new GameObject("wp" + i);
                wp.transform.SetParent(pathT);
                wp.transform.position = pc.waypoints[i].V;
            }

            // drone instance
            var droneT = dronesRoot.transform.Find("Drone_" + d);
            GameObject drone;
            if (droneT == null)
            {
                drone = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                drone.name = "Drone_" + d;
                drone.transform.SetParent(dronesRoot.transform);
            }
            else drone = droneT.gameObject;
            if (pc.waypoints.Length > 0) drone.transform.position = pc.waypoints[0].V;

            var pf = drone.GetComponent<DroneSim.DronePathFollower>();
            pf.pathRoot = pathT;
            pf.startOffset = pc.startOffset;

            // per-drone body tint via material asset (serializable, ML variety)
            if (pc.droneTint != null && (pc.droneTint.x + pc.droneTint.y + pc.droneTint.z) > 0.01f)
            {
                var tintMat = LoadOrCreateMat($"HK_DroneBody_{d}");
                tintMat.color = new Color(pc.droneTint.x, pc.droneTint.y, pc.droneTint.z);
                tintMat.SetFloat("_Smoothness", 0.35f);
                EditorUtility.SetDirty(tintMat);
                foreach (var r in drone.GetComponentsInChildren<Renderer>())
                    if (r.name == "Body" || r.name.StartsWith("Arm") || r.name == "CameraPod")
                        r.sharedMaterial = tintMat;
            }

            // clearance
            int n = pc.waypoints.Length;
            int hits = 0;
            string firstHit = "";
            if (n >= 4)
                for (int seg = 0; seg < n; seg++)
                {
                    Vector3 p0 = pc.waypoints[(seg - 1 + n) % n].V, p1 = pc.waypoints[seg].V,
                            p2 = pc.waypoints[(seg + 1) % n].V, p3 = pc.waypoints[(seg + 2) % n].V;
                    for (int s = 0; s < 24; s++)
                    {
                        float t = s / 24f, t2 = t * t, t3 = t2 * t;
                        Vector3 pos = 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
                        if (Physics.CheckSphere(pos, pc.clearanceRadius, mask))
                        { hits++; if (hits == 1) firstHit = $"seg{seg}@{pos:F0}"; }
                    }
                }
            report.Add($"path{d}: {n}wp hits={hits}{(hits > 0 ? " first=" + firstHit : "")}");
        }

        // remove surplus drones/paths beyond config
        for (int i = dronesRoot.transform.childCount - 1; i >= 0; i--)
        {
            var ch = dronesRoot.transform.GetChild(i);
            int idx;
            if (ch.name.StartsWith("Drone_") && int.TryParse(ch.name.Substring(6), out idx) && idx >= cfg.paths.Length)
                UnityEngine.Object.DestroyImmediate(ch.gameObject);
        }

        // RC-controlled drone (Phase 2): a hand-flown drone alongside the autopilot ones.
        // Lives under "Drones" so LabelPublisher captures + labels it like the rest (next id).
        var rcExisting = dronesRoot.transform.Find("Drone_RC");
        if (cfg.rc != null && cfg.rc.enabled)
        {
            GameObject rcDrone;
            if (rcExisting == null)
            {
                rcDrone = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                rcDrone.name = "Drone_RC";
                rcDrone.transform.SetParent(dronesRoot.transform);
            }
            else rcDrone = rcExisting.gameObject;

            // strip the spline follower, drive it with the RC controller instead
            var follower = rcDrone.GetComponent<DroneSim.DronePathFollower>();
            if (follower != null) UnityEngine.Object.DestroyImmediate(follower);
            var rcc = rcDrone.GetComponent<DroneSim.DroneRcController>()
                      ?? rcDrone.AddComponent<DroneSim.DroneRcController>();

            var rc = cfg.rc;
            rcc.spawnPos = rc.spawn.V; rcc.spawnYawDeg = rc.yawDeg;
            rcc.maxHorizSpeed = rc.maxHorizSpeed; rcc.maxClimbRate = rc.maxClimbRate;
            rcc.maxYawRate = rc.maxYawRate; rcc.accel = rc.accel;
            rcc.minAltitude = rc.minAltitude; rcc.maxAltitude = rc.maxAltitude;
            rcc.deadzone = rc.deadzone;
            rcc.rollCh = rc.rollCh; rcc.pitchCh = rc.pitchCh; rcc.thrCh = rc.thrCh; rcc.yawCh = rc.yawCh;
            rcc.invertRoll = rc.invertRoll; rcc.invertPitch = rc.invertPitch;
            rcc.invertThr = rc.invertThr; rcc.invertYaw = rc.invertYaw;
            rcc.throttleCentered = rc.throttleCentered;

            rcDrone.transform.position = rc.spawn.V;
            rcDrone.transform.rotation = Quaternion.Euler(0f, rc.yawDeg, 0f);
            report.Add($"rc: spawn={rc.spawn.V:F0} yaw={rc.yawDeg:F0}");
        }
        else if (rcExisting != null)
        {
            UnityEngine.Object.DestroyImmediate(rcExisting.gameObject); // RC disabled in config
        }

        Debug.Log("[HK] PATHS: " + string.Join(" | ", report));
        Save();
    }

    // ---------- 6. look ----------
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

        if (!string.IsNullOrEmpty(cfg.skyPanorama))
        {
            // HDRI panorama skybox (daytime clouds etc.) — cfg.skyPanorama = texture asset path
            var pano = AssetDatabase.LoadAssetAtPath<Texture>(cfg.skyPanorama);
            if (pano == null) Debug.LogError("[HK] LOOK: skyPanorama not found: " + cfg.skyPanorama);
            else
            {
                var skyP = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/HK_SkyPano.mat");
                if (skyP == null)
                {
                    skyP = new Material(Shader.Find("Skybox/Panoramic"));
                    AssetDatabase.CreateAsset(skyP, $"{MatDir}/HK_SkyPano.mat");
                }
                skyP.SetTexture("_MainTex", pano);
                skyP.SetFloat("_Exposure", cfg.skyPanoExposure);
                skyP.SetFloat("_Rotation", cfg.skyPanoRotation);
                EditorUtility.SetDirty(skyP);
                RenderSettings.skybox = skyP;
            }
        }
        else
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/HK_Sky.mat");
            if (sky == null)
            {
                sky = new Material(Shader.Find("Skybox/Procedural"));
                AssetDatabase.CreateAsset(sky, $"{MatDir}/HK_Sky.mat");
            }
            sky.SetFloat("_AtmosphereThickness", cfg.skyAtmosphere);
            sky.SetColor("_SkyTint", new Color(cfg.skyTint.x, cfg.skyTint.y, cfg.skyTint.z));
            sky.SetColor("_GroundColor", new Color(cfg.skyGround.x, cfg.skyGround.y, cfg.skyGround.z));
            RenderSettings.skybox = sky;
        }
        DynamicGI.UpdateEnvironment(); // ambient light follows the skybox

        // water plane (dusk reflections; covers the painted satellite water near the demo zone)
        var water = GameObject.Find("HarborWater");
        if (cfg.water != null && cfg.water.enabled)
        {
            if (water == null)
            {
                water = GameObject.CreatePrimitive(PrimitiveType.Plane);
                water.name = "HarborWater";
                UnityEngine.Object.DestroyImmediate(water.GetComponent<Collider>());
            }
            water.transform.position = cfg.water.center.V;
            water.transform.localScale = new Vector3(cfg.water.size.x / 10f, 1f, cfg.water.size.z / 10f);
            var wm = LoadOrCreateMat("HK_Water");
            wm.color = new Color(cfg.water.color.x, cfg.water.color.y, cfg.water.color.z);
            wm.SetFloat("_Smoothness", cfg.water.smoothness);
            // metallic 0: the plane must shade like the surround's painted sea (URP Lit,
            // albedo-driven) or its edge reads as a seam line at oblique view angles
            wm.SetFloat("_Metallic", 0f);
            EditorUtility.SetDirty(wm);
            water.GetComponent<MeshRenderer>().sharedMaterial = wm;
        }
        else if (water != null) UnityEngine.Object.DestroyImmediate(water);

        var rp = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        if (rp != null) rp.shadowDistance = cfg.shadowDistance;
        var qrp = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
        if (qrp != null) qrp.shadowDistance = cfg.shadowDistance;

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
        profile.components.RemoveAll(c => c == null); // heal profiles saved before the sub-asset fix below

        T Comp<T>() where T : VolumeComponent
        {
            if (!profile.TryGet(out T c))
            {
                c = profile.Add<T>(true);
                c.name = typeof(T).Name;
                // persist as a sub-asset — without this the component lives only in editor
                // memory: inspector tweaks vanish on domain reload and builds ship with NO post
                AssetDatabase.AddObjectToAsset(c, profile);
            }
            return c;
        }
        var bloom = Comp<Bloom>(); bloom.intensity.Override(cfg.bloom); bloom.threshold.Override(0.85f);
        var tone = Comp<Tonemapping>(); tone.mode.Override(TonemappingMode.ACES);
        var ca = Comp<ColorAdjustments>();
        ca.postExposure.Override(cfg.postExposure);
        ca.contrast.Override(cfg.contrast);
        ca.saturation.Override(cfg.saturation);
        var wb = Comp<WhiteBalance>(); wb.temperature.Override(cfg.temperature); wb.tint.Override(cfg.tint);
        var vig = Comp<Vignette>(); vig.intensity.Override(cfg.vignette);
        // camera-sensor character: subtle grain + edge CA make renders read as real footage
        var fg = Comp<FilmGrain>();
        fg.type.Override(FilmGrainLookup.Thin1);
        fg.intensity.Override(cfg.grain);
        fg.response.Override(cfg.grainResponse);
        var chrom = Comp<ChromaticAberration>(); chrom.intensity.Override(cfg.chromAb);
        // video-feed signature: lifted blacks (sensor floor) + barrel distortion of wide lenses
        var lgg = Comp<LiftGammaGain>();
        lgg.lift.Override(new Vector4(1f, 1f, 1f, cfg.blackLift));
        lgg.gamma.Override(new Vector4(1f, 1f, 1f, cfg.gamma));
        var ld = Comp<LensDistortion>();
        ld.intensity.Override(cfg.lensDistort);
        EditorUtility.SetDirty(profile);
        AssetDatabase.SaveAssets();
        Debug.Log("[HK] LOOK applied" + (cfg.water != null && cfg.water.enabled ? " (+water plane)" : ""));
        Save();
    }

    // ---------- 7. occlusion culling ----------
    // The dense city occludes most of itself from each fixed surveillance camera. Baking static
    // occlusion lets every camera skip geometry hidden behind buildings — pure GPU win, no visual
    // change. Re-run after geometry changes (Setup/Layout/Import). City tiles are already isStatic.
    [MenuItem("DroneSim/HK/7 Bake Occlusion")]
    public static void BakeOcclusion()
    {
        OpenScene();
        var city = City();
        if (city == null) { Debug.LogError("[HK] run Setup first"); return; }

        int flagged = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>())
        {
            var go = mf.gameObject;
            var flags = GameObjectUtility.GetStaticEditorFlags(go);
            GameObjectUtility.SetStaticEditorFlags(go,
                flags | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
            flagged++;
        }
        Save(); // occlusion bake reads the saved scene
        StaticOcclusionCulling.Compute();
        Debug.Log($"[HK] OCCLUSION: flagged {flagged} meshes, baked (data {StaticOcclusionCulling.umbraDataSize} bytes)");
        Save();
    }

    // ---------- utilities ----------
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
        cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
        Debug.Log($"[HK] VISTA @ {v.pos.V:F0} -> {v.lookAt.V:F0}");
    }

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

    /// offsets.json from the tile downloader: {"tiles":[{name,min_x,min_y,max_x,max_y},...]}
    [Serializable]
    class TileOffsets
    {
        [Serializable]
        public class Entry { public string name; public double min_x, min_y, max_x, max_y, tx, ty, tz;
                             // tx/ty = model_transform translation (authoritative); CSV mins as fallback
                             public double minX => tx != 0 ? tx : min_x; public double minY => ty != 0 ? ty : min_y; }
        public Entry[] tiles = new Entry[0];
        public int Count => tiles.Length;
        public System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, Entry>> All
        { get { foreach (var t in tiles) yield return new System.Collections.Generic.KeyValuePair<string, Entry>(t.name, t); } }
        public bool TryGet(string name, out Entry e)
        { foreach (var t in tiles) if (t.name == name) { e = t; return true; } e = null; return false; }
        public static TileOffsets Parse(string json) => JsonUtility.FromJson<TileOffsets>(json);
    }

    /// Import PlanD photo-realistic OBJ tiles (downloaded to ~/Downloads/pland_tiles/unpacked,
    /// one subfolder per tile with obj+mtl+jpg). Builds the photoreal scene: tiles under a
    /// "HongKongCity" root (so every other step works unchanged), Z-up fix, recentered to origin,
    /// colliders + Buildings layer, URP material conversion. Logs bounds + tri count for the perf gate.
    [MenuItem("DroneSim/HK/P Import Photo Tiles")]
    public static void ImportPhotoTiles()
    {
        string src = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Downloads/pland_tiles/unpacked";
        if (!Directory.Exists(src)) { Debug.LogError("[HK] PHOTO: missing " + src); return; }
        TileOffsets preOffsets = null;
        string preOffPath = src + "/offsets.json";
        if (File.Exists(preOffPath)) preOffsets = TileOffsets.Parse(File.ReadAllText(preOffPath));
        const string dstRoot = "Assets/HongKongPhoto/Tiles";
        Directory.CreateDirectory(dstRoot);
        int copied = 0;
        foreach (var dir in Directory.GetDirectories(src))
        {
            string tile = Path.GetFileName(dir);
            TileOffsets.Entry pre;
            if (preOffsets != null && !preOffsets.TryGet(tile, out pre)) continue; // not in the pick list
            string dst = $"{dstRoot}/{tile}";
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(dir))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext != ".obj" && ext != ".mtl" && ext != ".jpg" && ext != ".jpeg" && ext != ".png") continue;
                string to = $"{dst}/{Path.GetFileName(f)}";
                if (!File.Exists(to)) { File.Copy(f, to); copied++; }
            }
        }
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        // re-apply texture ceiling to already-imported atlases (postprocessor only runs on import)
        int retex = RetexTiles(dstRoot);
        Debug.Log($"[HK] PHOTO: copied={copied} files retex={retex}@{HKPhotoImportSettings.MaxTex}");

        // scene: create from SimScene template on first run
        if (!File.Exists(ScenePath))
        {
            if (!AssetDatabase.CopyAsset("Assets/Scenes/SimScene.unity", ScenePath))
            { Debug.LogError("[HK] PHOTO: scene template copy failed"); return; }
        }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // strip SimScene-template leftovers (block city, ground planes, stale paths/drones);
        // keep Sim (runtime bootstrap), SurveillanceRig, SpectatorCamera, lights.
        foreach (var junk in new[] { "Environment", "Ground", "FlightPaths", "Drones", "Drone", "HarborWater" })
        {
            GameObject g;
            while ((g = GameObject.Find(junk)) != null) UnityEngine.Object.DestroyImmediate(g);
        }
        var old = GameObject.Find("HongKongCity");
        if (old != null) UnityEngine.Object.DestroyImmediate(old);
        var root = new GameObject("HongKongCity");

        // ---- axis convention (verified by vertex scan, Jun 6) ----
        // All PlanD Kowloon tiles share ONE regional frame: x=east offset (m),
        // y=north offset (m, negative range), z=height above sea level (m).
        // With Unity's OBJ x-flip, Euler(-90,0,180) maps (E,N,h) -> Unity (E, h, N).
        // Vertex z=0 (sea level) lands at Unity y=0 — recenter xz only, never y
        // (bounds.min.y is poisoned by -200 m photogrammetry noise spikes).
        var rot = new GameObject("ZUpFix");
        rot.transform.SetParent(root.transform, false);
        rot.transform.localRotation = Quaternion.Euler(-90f, 0f, 180f);

        int tiles = 0; long tris = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { dstRoot }))
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (model == null) continue;
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            inst.transform.SetParent(rot.transform, false);
            tiles++;
        }
        if (tiles == 0) { Debug.LogError("[HK] PHOTO: no models imported"); return; }

        // recenter: combined bounds -> center.xz at origin; y untouched (sea level = 0)
        var rends = root.GetComponentsInChildren<Renderer>();
        Bounds b = rends[0].bounds;
        foreach (var r in rends) b.Encapsulate(r.bounds);
        root.transform.position = new Vector3(-b.center.x, 0f, -b.center.z);

        // colliders + layer + tri count + URP material sanity
        int layer = LayerMask.NameToLayer("Buildings");
        var urpLit = Shader.Find("Universal Render Pipeline/Lit");
        int converted = 0;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            mf.gameObject.layer = layer;
            mf.gameObject.isStatic = true;
            if (mf.sharedMesh != null)
            {
                tris += mf.sharedMesh.triangles.LongLength / 3;
                if (mf.GetComponent<MeshCollider>() == null) mf.gameObject.AddComponent<MeshCollider>();
            }
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            foreach (var m in mr.sharedMaterials)
            {
                if (m == null || m.shader == null) continue;
                if (m.shader != urpLit)
                {
                    var tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : (m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null);
                    m.shader = urpLit;
                    if (tex != null) m.SetTexture("_BaseMap", tex);
                    converted++;
                }
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.05f); // photo textures: matte
            }
        }
        Physics.SyncTransforms();
        var nb = rends[0].bounds; foreach (var r in rends) nb.Encapsulate(r.bounds);
        Debug.Log($"[HK] PHOTO: tiles={tiles} tris={tris} matsConverted={converted} bounds.center={nb.center:F0} bounds.size={nb.size:F0}");
        Save();
    }

    /// Re-apply HKPhotoImportSettings.MaxTex to the already-imported tile atlases (the
    /// postprocessor only runs at import time). Run after flipping MaxTex — e.g. the
    /// 8K ship import: batch via `-executeMethod HKSceneBuilder.RetexPhotoTiles`.
    [MenuItem("DroneSim/HK/P Retex Photo Tiles")]
    public static void RetexPhotoTiles()
    {
        int retex = RetexTiles("Assets/HongKongPhoto/Tiles");
        Debug.Log($"[HK] RETEX: {retex} atlases reimported @ {HKPhotoImportSettings.MaxTex}");
    }

    static int RetexTiles(string root)
    {
        int retex = 0;
        foreach (var tguid in AssetDatabase.FindAssets("t:Texture2D", new[] { root }))
        {
            var tpath = AssetDatabase.GUIDToAssetPath(tguid);
            var timp = AssetImporter.GetAtPath(tpath) as TextureImporter;
            if (timp != null && (timp.maxTextureSize != HKPhotoImportSettings.MaxTex || !timp.isReadable))
            { timp.maxTextureSize = HKPhotoImportSettings.MaxTex; timp.isReadable = true; timp.SaveAndReimport(); retex++; }
        }
        return retex;
    }

    /// Remove photogrammetry noise spikes: needle triangles with absurdly long edges
    /// (real geometry at 6 cm GSD tessellates finely; >35 m edges are reconstruction junk)
    /// plus anything sunk below y=-3 (underwater garbage). Textures untouched.
    [MenuItem("DroneSim/HK/P Trim Spikes")]
    public static void TrimSpikes()
    {
        OpenScene();
        // 10 m (was 18): the 18 m pass left floating ribbon/streamer junk over the
        // Peninsula/Mody quarter that photobombed street cams. 6 cm GSD geometry
        // tessellates well under 10 m; only reconstruction junk has edges this long.
        const float maxEdge = 10f;
        const float minY = -3f;
        float maxEdgeSq = maxEdge * maxEdge;
        var city = GameObject.Find("HongKongCity");
        if (city == null) { Debug.LogError("[HK] SPIKES: no HongKongCity"); return; }
        int trimmed = 0, meshes = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var l2w = mf.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            bool changed = false;
            var newMesh = UnityEngine.Object.Instantiate(mesh);
            for (int s = 0; s < newMesh.subMeshCount; s++)
            {
                var tris = newMesh.GetTriangles(s);
                var keep = new System.Collections.Generic.List<int>(tris.Length);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 a = l2w.MultiplyPoint3x4(verts[tris[i]]);
                    Vector3 b = l2w.MultiplyPoint3x4(verts[tris[i + 1]]);
                    Vector3 c = l2w.MultiplyPoint3x4(verts[tris[i + 2]]);
                    bool junk = (a - b).sqrMagnitude > maxEdgeSq || (b - c).sqrMagnitude > maxEdgeSq || (c - a).sqrMagnitude > maxEdgeSq
                                || (a.y < minY && b.y < minY && c.y < minY);
                    if (junk) { trimmed++; changed = true; continue; }
                    keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]);
                }
                newMesh.SetTriangles(keep, s);
            }
            if (changed)
            {
                newMesh.name = mesh.name + "_despike";
                mf.sharedMesh = newMesh;
                var mc = mf.GetComponent<MeshCollider>();
                if (mc != null) mc.sharedMesh = newMesh;
                meshes++;
            }
            else UnityEngine.Object.DestroyImmediate(newMesh);
        }
        Debug.Log($"[HK] SPIKES: removed {trimmed} tris across {meshes} meshes (edge>{maxEdge}m or y<{minY})");
        Save();
    }

    /// Remove baked photogrammetry water from the photo tiles: offshore triangles
    /// (world z south of the pier line, low altitude) are lumpy reconstructed sea —
    /// delete them so the clean water plane shows instead. Operates on scene instances
    /// (mesh copies), so re-run after any ImportPhotoTiles rebuild.
    [MenuItem("DroneSim/HK/P Trim Baked Water")]
    public static void TrimWater()
    {
        OpenScene();
        const float yMax = 5f; // baked sea lives low; never touch real structures
        var city = GameObject.Find("HongKongCity");
        if (city == null) { Debug.LogError("[HK] TRIM: no HongKongCity"); return; }
        int trimmed = 0, meshes = 0, unreadableTex = 0;
        foreach (var mf in city.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr == null) continue;
            var l2w = mf.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            var uvs = mesh.uv;
            bool changed = false;
            var newMesh = UnityEngine.Object.Instantiate(mesh);
            for (int s = 0; s < newMesh.subMeshCount && s < mr.sharedMaterials.Length; s++)
            {
                var mat = mr.sharedMaterials[s];
                var tex = mat != null ? mat.GetTexture("_BaseMap") as Texture2D : null;
                if (tex == null) continue;
                if (!tex.isReadable) { unreadableTex++; continue; }
                var tris = newMesh.GetTriangles(s);
                var keep = new System.Collections.Generic.List<int>(tris.Length);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    int a = tris[i], b2 = tris[i + 1], c2 = tris[i + 2];
                    Vector3 w = l2w.MultiplyPoint3x4((verts[a] + verts[b2] + verts[c2]) / 3f);
                    bool water = false;
                    if (w.y < yMax && uvs.Length > 0)
                    {
                        Vector2 uv = (uvs[a] + uvs[b2] + uvs[c2]) / 3f;
                        Color px = tex.GetPixelBilinear(uv.x, uv.y);
                        // baked harbour water is cyan: green & blue clearly above red, blue tracks green
                        // (foliage is green with LOW blue; concrete/asphalt is near-grey)
                        water = px.g > px.r + 0.05f && px.b > px.r && px.b > 0.75f * px.g && px.g > 0.25f;
                    }
                    if (water) { trimmed++; changed = true; continue; }
                    keep.Add(a); keep.Add(b2); keep.Add(c2);
                }
                newMesh.SetTriangles(keep, s);
            }
            if (changed)
            {
                newMesh.name = mesh.name + "_trim";
                mf.sharedMesh = newMesh;
                var mc = mf.GetComponent<MeshCollider>();
                if (mc != null) mc.sharedMesh = newMesh;
                meshes++;
            }
            else UnityEngine.Object.DestroyImmediate(newMesh);
        }
        Debug.Log($"[HK] TRIM: removed {trimmed} water tris across {meshes} meshes (unreadableTex={unreadableTex})");
        Save();
    }

    /// Distant surround for the photoreal patch: the stylized Hong_Kong.fbx placed so its
    /// Island skyline + Victoria Peak sit across the harbour (~1.3 km south) and its far
    /// Kowloon fills the north. Hole-punched where the photoreal tiles live. Pure visual:
    /// no colliders, NOT on the Buildings layer (occlusion/probes/path checks ignore it).
    [MenuItem("DroneSim/HK/P Import Surround")]
    public static void ImportSurround()
    {
        OpenScene();
        var old = GameObject.Find("Surround");
        if (old != null) UnityEngine.Object.DestroyImmediate(old);

        const string SurFbx = "Assets/HongKongSurround/source/Hong_Kong.fbx"; // fresh unaltered package
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(SurFbx);
        if (model == null) { Debug.LogError("[HK] SURROUND: missing " + SurFbx); return; }
        var root = new GameObject("Surround");
        var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
        inst.transform.SetParent(root.transform, false);
        // stylized model: island/Peak on its +z side; rotate 180 so they land SOUTH of us,
        // then shift so its harbour wraps our patch. Tuned by screenshot iteration.
        root.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
        // Geo-solved v2 (Jun 7, texture-georeferenced): the map texture is 8192px over the
        // 15440 m Map mesh = 1.8848 m/px, so texture px -> world is exact. Matching the
        // stylized TST tip (tex 4090,2910) and HKCEC (tex 4300,3510) against the PlanD
        // HK1980 georeference (unityX = E−836137.86, unityZ = N−817460.79) showed the old
        // T=(900,1240) put the whole stylized city +1322 m east / +255 m north of reality —
        // the photoreal patch floated in displaced sea. Corrected T=(−422,985): stylized
        // Kowloon land now butts the patch's north/east edges, island shore stays ~z −1380.
        // NB the map imagery is pre-1998 (Kai Tak intact, no WKCD/ICC reclamation), so
        // small coastline-era mismatches at the seams are expected and acceptable.
        root.transform.position = new Vector3(-422f, 0f, 985f);

        // bind the model's ORIGINAL textures explicitly: the FBX's embedded materials
        // import without bindings for some slots (ground/Map is white). Copies of the
        // embedded originals get the right Color texture by name; dusk HK_* mats unused.
        int remapped = 0;
        foreach (var g in AssetDatabase.FindAssets("HK_Sur_", new[] { MatDir }))
            AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(g)); // purge stale copies from earlier attempts
        System.Func<string, Texture2D> findTex = matName =>
        {
            if (matName.IndexOf("Map", StringComparison.OrdinalIgnoreCase) >= 0
                || matName.IndexOf("Hong", StringComparison.OrdinalIgnoreCase) >= 0)
                return AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/HongKongSurround/textures/Hong_Kong_Color.jpeg");
            for (int w = 1; w <= 6; w++)
                if (matName.IndexOf("Wall" + w, StringComparison.OrdinalIgnoreCase) >= 0)
                    return AssetDatabase.LoadAssetAtPath<Texture2D>($"Assets/HongKongSurround/textures/Wall{w}_Color.png");
            return null;
        };
        foreach (var r in root.GetComponentsInChildren<Renderer>())
        {
            var mats = r.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null) continue;
                string baseName = mats[i].name.Replace(" (Instance)", "");
                string surPath = $"{MatDir}/HK_Sur_{baseName}.mat";
                var sur = AssetDatabase.LoadAssetAtPath<Material>(surPath);
                if (sur == null)
                {
                    sur = new Material(mats[i]);
                    // only fill slots the importer left unbound — otherwise keep the original binding
                    var hasTex = sur.HasProperty("_BaseMap") && sur.GetTexture("_BaseMap") != null;
                    if (!hasTex)
                    {
                        var tex = findTex(baseName);
                        if (tex != null) { sur.SetTexture("_BaseMap", tex); sur.color = Color.white; }
                    }
                    sur.SetFloat("_Smoothness", 0.15f);
                    sur.SetColor("_EmissionColor", Color.black);
                    sur.DisableKeyword("_EMISSION");
                    AssetDatabase.CreateAsset(sur, surPath);
                }
                mats[i] = sur; remapped++;
            }
            r.sharedMaterials = mats;
        }
        AssetDatabase.SaveAssets();

        // hole-punch: drop triangles whose centroid falls inside the photoreal patch box.
        // North edge: the PlanD grid is ~1° skewed vs our axes — row+003 tile edges slant
        // z+440 (west) → z+482 (east). With the corrected surround placement the north
        // neighbour is stylized LAND, so cut at +438: photoreal ground (y≈2-8 m) overlaps
        // and hides the stylized ground underneath (no z-fight), whereas any gap would show
        // as a dark void slit. South: extend to the island shore (z≈−1410) so the water
        // plane owns the whole basin — removes painted sea + boat props mid-harbour.
        var patchMin = new Vector2(-1258f, -1405f);
        var patchMax = new Vector2(1258f, 438f);
        int trimmed = 0;
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = mf.sharedMesh;
            if (mesh == null) continue;
            var l2w = mf.transform.localToWorldMatrix;
            var verts = mesh.vertices;
            bool changed = false;
            var newMesh = UnityEngine.Object.Instantiate(mesh);
            for (int s = 0; s < newMesh.subMeshCount; s++)
            {
                var tris = newMesh.GetTriangles(s);
                var keep = new System.Collections.Generic.List<int>(tris.Length);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 c = l2w.MultiplyPoint3x4((verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f);
                    if (c.x > patchMin.x && c.x < patchMax.x && c.z > patchMin.y && c.z < patchMax.y)
                    { trimmed++; changed = true; continue; }
                    keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]);
                }
                newMesh.SetTriangles(keep, s);
            }
            if (changed)
            {
                newMesh.name = mesh.name + "_hole";
                mf.sharedMesh = newMesh;
            }
            else UnityEngine.Object.DestroyImmediate(newMesh);
            // visual only — no colliders, default layer
            var mc = mf.GetComponent<MeshCollider>();
            if (mc != null) UnityEngine.Object.DestroyImmediate(mc);
        }
        Debug.Log($"[HK] SURROUND: placed, remapped={remapped} mats, holePunched={trimmed} tris");
        Save();
    }

    /// ASCII height map of the photoreal patch (recentered frame): rows north->south.
    /// digits = height bands (0:<2m ground, 1:2-8m, 2:8-20m, 3:20-50m, 4:50-100m, 5:>100m), '.' = no geometry.
    [MenuItem("DroneSim/HK/P Probe Photo Zone")]
    public static void ProbePhoto()
    {
        OpenScene();
        Physics.SyncTransforms();
        int mask = LayerMask.GetMask("Buildings");
        var sb = new System.Text.StringBuilder();
        sb.Append("[HK] PZONE x-500..500 step25 (rows z+200 down to z-450):\n");
        for (float z = 200f; z >= -450f; z -= 25f)
        {
            sb.Append($"z{z,5:F0} ");
            for (float x = -500f; x <= 500f; x += 25f)
            {
                char c = '.';
                RaycastHit hit;
                if (Physics.Raycast(new Vector3(x, 800f, z), Vector3.down, out hit, 1200f, mask))
                {
                    float h = hit.point.y;
                    c = h < 2f ? '0' : h < 8f ? '1' : h < 20f ? '2' : h < 50f ? '3' : h < 100f ? '4' : '5';
                }
                sb.Append(c);
            }
            sb.Append('\n');
        }
        Debug.Log(sb.ToString());
    }

    /// ASCII terrain map of the demo zone, ignoring our own hero/prop geometry.
    /// '#' = model ground above 2 m, '~' = low ground 0..2 m (reads as water/shore), '.' = no hit.
    [MenuItem("DroneSim/HK/0 Probe Demo Zone")]
    public static void ProbeZone()
    {
        OpenScene();
        Physics.SyncTransforms();
        int mask = LayerMask.GetMask("Buildings");
        var sb = new System.Text.StringBuilder();
        sb.Append("[HK] ZONE x200..1800 step50 (rows z2800 down to z1300):\n");
        for (float z = 2800f; z >= 1300f; z -= 50f)
        {
            sb.Append($"z{z,4:F0} ");
            for (float x = 200f; x <= 1800f; x += 50f)
            {
                char c = '.';
                float best = float.MaxValue;
                foreach (var hit in Physics.RaycastAll(new Vector3(x, 1400f, z), Vector3.down, 2900f, mask))
                {
                    string n = hit.collider.transform.name;
                    if (n.StartsWith("Hero_") || n.StartsWith("Prop_") || n.StartsWith("Drone_") || n == "HarborWater"
                        || n == "pole" || n == "housing" || n == "arm" || n == "bracket") continue;
                    if (hit.distance < best) { best = hit.distance; c = hit.point.y > 2f ? '#' : '~'; }
                }
                sb.Append(c);
            }
            sb.Append('\n');
        }
        Debug.Log(sb.ToString());
    }

    // ---------- 8. hero waterfront towers ----------
    static readonly string[] HeroSets = { "Facade018A", "Facade019A", "Facade020A", "Facade020B", "Facade013" };
    const string HeroTexDir = "Assets/HongKong/HeroTextures";
    const float FacadeMetersPerTile = 14f; // one texture tile ≈ 4 stories

    [MenuItem("DroneSim/HK/8 Build Hero Set")]
    public static void BuildHeroSet()
    {
        OpenScene();
        var cfg = Load();
        var look = cfg.look;
        var root = GameObject.Find("HeroSet");
        if (root != null) UnityEngine.Object.DestroyImmediate(root);
        root = new GameObject("HeroSet");

        var roofMat = LoadOrCreateMat("HK_HeroRoof", m =>
        {
            m.color = new Color(0.16f, 0.16f, 0.17f);
            m.SetFloat("_Smoothness", 0.4f);
        });

        Physics.SyncTransforms();
        int bLayer = LayerMask.NameToLayer("Buildings");
        int built = 0;
        for (int i = 0; i < cfg.heroes.Length; i++)
        {
            var hd = cfg.heroes[i];
            string setName = HeroSets[Mathf.Clamp(hd.mat, 0, HeroSets.Length - 1)];

            // per-tower material (unique tiling so windows stay ~real size)
            var mat = LoadOrCreateMat($"HK_Hero_{i}");
            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{HeroTexDir}/{setName}_Color.jpg");
            var normTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{HeroTexDir}/{setName}_NormalGL.jpg");
            var emisTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{HeroTexDir}/{setName}_Emission.jpg");
            if (baseTex == null) { Debug.LogError($"[HK] hero textures missing for {setName} — run the import step"); return; }
            mat.SetTexture("_BaseMap", baseTex);
            if (normTex != null) { mat.SetTexture("_BumpMap", normTex); mat.EnableKeyword("_NORMALMAP"); }
            mat.SetFloat("_Smoothness", 0.55f);
            mat.SetFloat("_Metallic", 0.1f);
            if (emisTex != null && look.emissionIntensity > 0f)
            {
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                mat.SetTexture("_EmissionMap", emisTex);
                var t = look.emissionTint.V * look.emissionIntensity;
                mat.SetColor("_EmissionColor", new Color(t.x, t.y, t.z));
            }
            mat.SetTextureScale("_BaseMap", new Vector2(Mathf.Max(1f, hd.w / FacadeMetersPerTile),
                                                        Mathf.Max(1f, hd.h / FacadeMetersPerTile)));
            EditorUtility.SetDirty(mat);

            float groundY = 5f;
            RaycastHit hit;
            if (Physics.Raycast(new Vector3(hd.x, 300f, hd.z), Vector3.down, out hit, 600f, LayerMask.GetMask("Buildings")))
                groundY = hit.point.y;

            bool isDeck = hd.h <= 12f; // low flat entries are podium/deck slabs, not towers
            var bodyMat = isDeck ? roofMat : mat;
            GameObject Tower(string n, float w, float d, float h, float yBase)
            {
                var t = GameObject.CreatePrimitive(PrimitiveType.Cube);
                t.name = n;
                t.transform.SetParent(root.transform);
                t.transform.position = new Vector3(hd.x, yBase + h * 0.5f, hd.z);
                t.transform.rotation = Quaternion.Euler(0, hd.rotY, 0);
                t.transform.localScale = new Vector3(w, h, d);
                t.layer = bLayer;
                t.isStatic = true;
                t.GetComponent<MeshRenderer>().sharedMaterial = bodyMat;
                return t;
            }

            float mainH = hd.setback ? hd.h * 0.75f : hd.h;
            Tower($"Hero_{i}", hd.w, hd.d, mainH, groundY);
            if (hd.setback)
                Tower($"Hero_{i}_top", hd.w * 0.65f, hd.d * 0.65f, hd.h * 0.25f, groundY + mainH);
            Physics.SyncTransforms(); // later towers/poles can raycast onto the deck
            if (isDeck) { built++; continue; } // no roof slab on decks

            // roof slab
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = $"Hero_{i}_roof";
            roof.transform.SetParent(root.transform);
            float topY = groundY + hd.h;
            float rw = hd.setback ? hd.w * 0.65f : hd.w;
            float rd = hd.setback ? hd.d * 0.65f : hd.d;
            roof.transform.position = new Vector3(hd.x, topY + 0.4f, hd.z);
            roof.transform.rotation = Quaternion.Euler(0, hd.rotY, 0);
            roof.transform.localScale = new Vector3(rw + 1.5f, 0.8f, rd + 1.5f);
            roof.layer = bLayer;
            roof.isStatic = true;
            roof.GetComponent<MeshRenderer>().sharedMaterial = roofMat;
            built++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[HK] HEROES: built={built}");
        Save();
    }

    /// Copy + rename the downloaded ambientCG maps into HeroTextures (run once after download).
    [MenuItem("DroneSim/HK/8 Import Hero Textures")]
    public static void ImportHeroTextures()
    {
        if (!AssetDatabase.IsValidFolder(HeroTexDir)) AssetDatabase.CreateFolder("Assets/HongKong", "HeroTextures");
        string src = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/Downloads/heroassets/unpacked";
        int copied = 0;
        foreach (var set in HeroSets)
        {
            foreach (var kind in new[] { "Color", "NormalGL", "Emission" })
            {
                string from = $"{src}/{set}/{set}_2K-JPG_{kind}.jpg";
                string to = $"{HeroTexDir}/{set}_{kind}.jpg";
                if (File.Exists(from) && !File.Exists(to)) { File.Copy(from, to); copied++; }
            }
        }
        AssetDatabase.Refresh();
        // mark normals
        foreach (var set in HeroSets)
        {
            var imp = AssetImporter.GetAtPath($"{HeroTexDir}/{set}_NormalGL.jpg") as TextureImporter;
            if (imp != null && imp.textureType != TextureImporterType.NormalMap)
            { imp.textureType = TextureImporterType.NormalMap; imp.SaveAndReimport(); }
        }
        Debug.Log($"[HK] HERO TEXTURES: copied={copied}");
    }

    // ---------- 7. build settings ----------
    [MenuItem("DroneSim/HK/7 Set As Build Scene")]
    public static void SetBuildScene()
    {
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        Debug.Log("[HK] build scene -> " + ScenePath);
    }
}
