using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace DroneSim
{
    /// <summary>
    /// Renders each enabled surveillance camera into its own RenderTexture every sim frame,
    /// reads frames back asynchronously (never stalls the GPU) and hands them to the
    /// per-camera ffmpeg pipe. Camera placement is authored in the scene; this component
    /// matches scene cameras to config entries by name.
    /// </summary>
    public class CaptureManager : MonoBehaviour
    {
        [Tooltip("Parent of the surveillance cameras (cam0..camN)")]
        public Transform rigRoot;

        public class CamSlot
        {
            public Camera cam;
            public RenderTexture rt;
            public FfmpegPipe pipe;
            public string name;
        }

        readonly List<CamSlot> _slots = new List<CamSlot>();
        public IReadOnlyList<CamSlot> Slots => _slots;
        bool _sizeWarned;
        int _camsPerFrame; // how many cameras render each frame (round-robin when < slot count)
        int _cursor;       // rotation start index for the current frame

        void Start()
        {
            var cfg = SimConfig.Instance;
            foreach (var entry in cfg.cameras)
            {
                if (!entry.enabled) continue;
                var t = rigRoot != null ? rigRoot.Find(entry.name) : null;
                var cam = t != null ? t.GetComponent<Camera>() : null;
                if (cam == null)
                {
                    Debug.LogError($"[Sim] Camera '{entry.name}' not found under '{(rigRoot ? rigRoot.name : "<null>")}'");
                    continue;
                }

                var rt = new RenderTexture(cfg.capture.width, cfg.capture.height, 24,
                                           RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { antiAliasing = 1, name = $"rt_{entry.name}" };
                rt.Create();
                cam.targetTexture = rt;
                cam.enabled = true; // sim runs at capture fps, so every rendered frame is streamed

                var pipe = new FfmpegPipe(entry.name, cfg.capture.width, cfg.capture.height,
                                          cfg.capture.fps, cfg.capture.bitrate_kbps,
                                          cfg.encoder.mode, cfg.encoder.ffmpeg_path,
                                          cfg.rtsp.base_url.TrimEnd('/') + "/" + entry.name);
                _slots.Add(new CamSlot { cam = cam, rt = rt, pipe = pipe, name = entry.name });
            }

            // When the loop runs faster than the stream rate (render_fps > fps), render only a slice
            // of the cameras each frame so each still streams at ~fps — keeps the live pilot view fast.
            int n = _slots.Count;
            int loopFps = cfg.capture.render_fps > cfg.capture.fps ? cfg.capture.render_fps : cfg.capture.fps;
            if (cfg.capture.cams_per_frame > 0)
                _camsPerFrame = Mathf.Clamp(cfg.capture.cams_per_frame, 1, Mathf.Max(1, n));
            else if (loopFps > cfg.capture.fps && cfg.capture.fps > 0)
                _camsPerFrame = Mathf.Clamp(Mathf.CeilToInt(n * (float)cfg.capture.fps / loopFps), 1, Mathf.Max(1, n));
            else
                _camsPerFrame = n;

            Debug.Log($"[Sim] Capture: {n} camera(s) active, {_camsPerFrame}/frame ({(_camsPerFrame < n ? "round-robin" : "all")})");
            StartCoroutine(CaptureLoop());
        }

        void Update()
        {
            int n = _slots.Count;
            if (n == 0) return;
            if (_camsPerFrame >= n) { for (int i = 0; i < n; i++) _slots[i].cam.enabled = true; return; }
            for (int i = 0; i < n; i++) _slots[i].cam.enabled = false;
            for (int k = 0; k < _camsPerFrame; k++) _slots[(_cursor + k) % n].cam.enabled = true;
            _cursor = (_cursor + _camsPerFrame) % n;
        }

        IEnumerator CaptureLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait; // cameras enabled this frame have rendered their state
                foreach (var s in _slots)
                {
                    if (!s.cam.enabled) continue; // round-robin: only read back cameras rendered this frame
                    var slot = s; // closure copy
                    AsyncGPUReadback.Request(slot.rt, 0, TextureFormat.RGBA32, req =>
                    {
                        if (req.hasError)
                        {
                            Debug.LogWarning($"[Sim] {slot.name}: GPU readback error");
                            return;
                        }
                        var data = req.GetData<byte>();
                        var buf = slot.pipe.RentBuffer();
                        if (data.Length < buf.Length)
                        {
                            if (!_sizeWarned) { _sizeWarned = true; Debug.LogError($"[Sim] {slot.name}: readback size {data.Length} < expected {buf.Length} — stream disabled"); }
                            return;
                        }
                        NativeArray<byte>.Copy(data, 0, buf, 0, buf.Length);
                        slot.pipe.PushFrame(buf);
                    });
                }
            }
        }

        void OnDestroy()
        {
            AsyncGPUReadback.WaitAllRequests(); // flush pending callbacks before tearing down pipes
            foreach (var s in _slots)
            {
                s.pipe?.Dispose();
                if (s.cam != null) s.cam.targetTexture = null;
                if (s.rt != null) { s.rt.Release(); Destroy(s.rt); }
            }
            _slots.Clear();
        }
    }
}
