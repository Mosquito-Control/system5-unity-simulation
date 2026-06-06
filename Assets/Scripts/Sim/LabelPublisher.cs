using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace DroneSim
{
    /// <summary>
    /// Computes ground truth every sim frame and publishes ONE UDP JSON datagram:
    /// 3D drone pose(s) + per-camera 2D bboxes + camera intrinsics/extrinsics,
    /// stamped with the same frame_id as the captured video frames (plan §4.2).
    /// Conventions: world = Unity (left-handed, +Y up, meters); image = origin top-left,
    /// matching the delivered (vflipped) video.
    /// </summary>
    public class LabelPublisher : MonoBehaviour
    {
        public Transform dronesRoot;
        public CaptureManager capture;

        [Serializable] public class DroneState { public int id; public float[] pos_w; public float[] vel_w; public float[] rot_q; }
        [Serializable] public class Detection { public int drone_id; public float[] bbox_xyxy; public float[] center_px; public float dist_m; public bool visible; public bool truncated; }
        [Serializable] public class CamBlock { public string name; public int img_w; public int img_h; public float[] pos_w; public float[] rot_q; public float[] K; public Detection[] detections; }
        [Serializable] public class LabelPacket { public int v = 1; public long frame_id; public long t_unix_ms; public double t_sim; public DroneState[] drones; public CamBlock[] cameras; }

        class DroneRef { public int id; public Transform tf; public Renderer[] renderers; public Vector3 lastPos; public Vector3 vel; }

        UdpClient _udp;
        int _minBboxPx;
        int _buildingsMask;
        readonly List<DroneRef> _drones = new List<DroneRef>();
        readonly List<Detection> _detScratch = new List<Detection>(4);

        void Start()
        {
            var cfg = SimConfig.Instance;
            _minBboxPx = Mathf.Max(1, cfg.labels.min_bbox_px);
            _buildingsMask = LayerMask.GetMask("Buildings");
            _udp = new UdpClient();
            _udp.Connect(cfg.labels.host, cfg.labels.port);

            int id = 0;
            foreach (Transform child in dronesRoot)
            {
                if (!child.gameObject.activeInHierarchy) continue;
                _drones.Add(new DroneRef
                {
                    id = id++,
                    tf = child,
                    renderers = child.GetComponentsInChildren<Renderer>(),
                    lastPos = child.position
                });
            }
            Debug.Log($"[Sim] Labels: {_drones.Count} drone(s) -> udp://{cfg.labels.host}:{cfg.labels.port}");
        }

        void LateUpdate() // after drone movement (Update), same transform state the cameras render
        {
            if (_drones.Count == 0 || capture == null || capture.Slots.Count == 0) return;

            float dt = Mathf.Max(Time.deltaTime, 1e-5f);
            var packet = new LabelPacket
            {
                frame_id = SimBootstrap.FrameId,
                t_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                t_sim = Time.timeAsDouble,
                drones = new DroneState[_drones.Count],
                cameras = new CamBlock[capture.Slots.Count]
            };

            for (int i = 0; i < _drones.Count; i++)
            {
                var d = _drones[i];
                d.vel = (d.tf.position - d.lastPos) / dt;
                d.lastPos = d.tf.position;
                var q = d.tf.rotation;
                packet.drones[i] = new DroneState
                {
                    id = d.id,
                    pos_w = ToArr(d.tf.position),
                    vel_w = ToArr(d.vel),
                    rot_q = new[] { q.x, q.y, q.z, q.w }
                };
            }

            for (int c = 0; c < capture.Slots.Count; c++)
            {
                var slot = capture.Slots[c];
                var cam = slot.cam;
                int w = cam.pixelWidth, h = cam.pixelHeight;
                var cq = cam.transform.rotation;

                _detScratch.Clear();
                foreach (var d in _drones)
                    if (TryProject(cam, d, w, h, out var det)) _detScratch.Add(det);

                packet.cameras[c] = new CamBlock
                {
                    name = slot.name,
                    img_w = w,
                    img_h = h,
                    pos_w = ToArr(cam.transform.position),
                    rot_q = new[] { cq.x, cq.y, cq.z, cq.w },
                    K = Intrinsics(cam, w, h),
                    detections = _detScratch.ToArray()
                };
            }

            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet));
            try { _udp.Send(bytes, bytes.Length); }
            catch (SocketException) { /* no listener yet — fine */ }
        }

        bool TryProject(Camera cam, DroneRef d, int w, int h, out Detection det)
        {
            det = null;
            if (d.renderers.Length == 0) return false;

            Bounds b = d.renderers[0].bounds;
            for (int i = 1; i < d.renderers.Length; i++) b.Encapsulate(d.renderers[i].bounds);

            Vector3 c = b.center, e = b.extents;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            int behind = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x,
                                             (i & 2) == 0 ? -e.y : e.y,
                                             (i & 4) == 0 ? -e.z : e.z);
                var sp = cam.WorldToScreenPoint(corner);
                if (sp.z <= 0f) { behind++; continue; }
                if (sp.x < minX) minX = sp.x;
                if (sp.x > maxX) maxX = sp.x;
                if (sp.y < minY) minY = sp.y;
                if (sp.y > maxY) maxY = sp.y;
            }
            if (behind == 8) return false;

            // screen (origin bottom-left) -> image (origin top-left, matches delivered video)
            float x1 = minX, y1 = h - maxY, x2 = maxX, y2 = h - minY;
            bool truncated = behind > 0 || x1 < 0 || y1 < 0 || x2 > w || y2 > h;
            x1 = Mathf.Clamp(x1, 0, w); x2 = Mathf.Clamp(x2, 0, w);
            y1 = Mathf.Clamp(y1, 0, h); y2 = Mathf.Clamp(y2, 0, h);
            if (x2 - x1 < _minBboxPx || y2 - y1 < _minBboxPx) return false;

            if (IsOccluded(cam.transform.position, b)) return false;

            det = new Detection
            {
                drone_id = d.id,
                bbox_xyxy = new[] { x1, y1, x2, y2 },
                center_px = new[] { (x1 + x2) * 0.5f, (y1 + y2) * 0.5f },
                dist_m = Vector3.Distance(cam.transform.position, b.center),
                visible = true,
                truncated = truncated
            };
            return true;
        }

        bool IsOccluded(Vector3 from, Bounds b)
        {
            if (RayClear(from, b.center)) return false;
            Vector3 e = b.extents; // 4 alternating corners suffice for a partial-visibility test
            if (RayClear(from, b.center + new Vector3(e.x, e.y, e.z))) return false;
            if (RayClear(from, b.center + new Vector3(-e.x, e.y, -e.z))) return false;
            if (RayClear(from, b.center + new Vector3(e.x, -e.y, -e.z))) return false;
            if (RayClear(from, b.center + new Vector3(-e.x, -e.y, e.z))) return false;
            return true;
        }

        bool RayClear(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 1e-4f) return true;
            return !Physics.Raycast(from, d / len, len, _buildingsMask);
        }

        static float[] ToArr(Vector3 v) => new[] { v.x, v.y, v.z };

        static float[] Intrinsics(Camera cam, int w, int h)
        {
            // Ideal pinhole, zero distortion. fx = fy (square pixels), principal point at center.
            float fy = (h * 0.5f) / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return new[] { fy, 0f, w * 0.5f, 0f, fy, h * 0.5f, 0f, 0f, 1f };
        }

        void OnDestroy()
        {
            _udp?.Close();
        }
    }
}
