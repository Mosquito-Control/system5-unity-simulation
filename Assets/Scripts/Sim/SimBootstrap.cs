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
            Application.targetFrameRate = cfg.capture.fps; // sim runs AT the capture rate (plan §3.7)
            Application.runInBackground = true;
            FrameId = 0;
            Debug.Log($"[Sim] Bootstrap: {cfg.capture.width}x{cfg.capture.height}@{cfg.capture.fps}fps, " +
                      $"rtsp={cfg.rtsp.base_url}, labels=udp://{cfg.labels.host}:{cfg.labels.port}");
        }

        void Update()
        {
            FrameId++; // executes before all other scripts (DefaultExecutionOrder -100)
        }
    }
}
