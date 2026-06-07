using UnityEngine;

namespace DroneSim
{
    /// <summary>
    /// Entry point: locks the sim loop to the capture rate and owns the global frame id.
    /// Every captured frame and every labels datagram in the same sim frame share one FrameId,
    /// which is how the ML side aligns video with ground truth.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class SimBootstrap : MonoBehaviour
    {
        public static long FrameId { get; private set; }

        void Awake()
        {
            var cfg = SimConfig.Instance;
            QualitySettings.vSyncCount = 0;
            // Decouple the main loop from the per-stream capture rate: when render_fps > capture.fps,
            // the loop (and the live pilot view) runs faster while CaptureManager renders the cameras
            // round-robin so each stream still lands at ~capture.fps. render_fps <= 0 keeps the old
            // behaviour (every camera every frame at capture.fps).
            int loopFps = cfg.capture.render_fps > cfg.capture.fps ? cfg.capture.render_fps : cfg.capture.fps;
            Application.targetFrameRate = loopFps;
            Application.runInBackground = true;
            FrameId = 0;
            Debug.Log($"[Sim] Bootstrap: {cfg.capture.width}x{cfg.capture.height} stream@{cfg.capture.fps}fps loop@{loopFps}fps, " +
                      $"rtsp={cfg.rtsp.base_url}, labels=udp://{cfg.labels.host}:{cfg.labels.port}");
        }

        void Update()
        {
            FrameId++; // executes before all other scripts (DefaultExecutionOrder -100)
        }
    }
}
