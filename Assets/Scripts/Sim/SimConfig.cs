using System;
using System.IO;
using UnityEngine;

namespace DroneSim
{
    [Serializable] public class CaptureConfig { public int width = 1280; public int height = 720; public int fps = 15; public int bitrate_kbps = 4000; public int render_fps = 0; public int cams_per_frame = 0; }
    [Serializable] public class EncoderConfig { public string mode = "auto"; public string ffmpeg_path = "ffmpeg"; }
    [Serializable] public class RtspConfig { public string base_url = "rtsp://127.0.0.1:8554/"; }
    [Serializable] public class LabelsConfig { public string host = "127.0.0.1"; public int port = 9870; public bool embed_frame_id = false; public int min_bbox_px = 2; }
    [Serializable] public class CameraEntry { public string name; public bool enabled = true; }
    [Serializable] public class DroneConfig { public float scale = 2.0f; public float path_speed_mps = 8.0f; }
    [Serializable] public class ControlConfig { public int udp_port = 9871; public int failsafe_ms = 250; public string mode = "autopilot"; }

    /// <summary>
    /// Runtime configuration, loaded once from StreamingAssets/simconfig.json.
    /// Single source of truth for capture/stream/label parameters (see SIMULATION_PLAN.md §6).
    /// </summary>
    [Serializable]
    public class SimConfig
    {
        public CaptureConfig capture = new CaptureConfig();
        public EncoderConfig encoder = new EncoderConfig();
        public RtspConfig rtsp = new RtspConfig();
        public LabelsConfig labels = new LabelsConfig();
        public CameraEntry[] cameras = new CameraEntry[0];
        public DroneConfig drone = new DroneConfig();
        public ControlConfig control = new ControlConfig();

        static SimConfig _instance;
        public static SimConfig Instance => _instance ?? (_instance = Load());

        static SimConfig Load()
        {
            string path = Path.Combine(Application.streamingAssetsPath, "simconfig.json");
            try
            {
                if (File.Exists(path))
                {
                    var cfg = JsonUtility.FromJson<SimConfig>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        Debug.Log($"[Sim] Config loaded: {path}");
                        return cfg;
                    }
                }
                Debug.LogWarning($"[Sim] Config missing/invalid at {path} — using defaults");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Sim] Config load failed ({e.Message}) — using defaults");
            }
            return new SimConfig();
        }
    }
}
