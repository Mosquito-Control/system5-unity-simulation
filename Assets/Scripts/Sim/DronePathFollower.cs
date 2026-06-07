using System.Collections.Generic;
using UnityEngine;

namespace DroneSim
{
    /// <summary>
    /// Phase-1 kinematic flight: closed Catmull-Rom spline through the waypoint children
    /// of pathRoot, constant speed via arc-length table, attitude faked from acceleration.
    /// Phase 2 replaces this with the physics + RC stack (autopilot mode keeps using it).
    /// </summary>
    public class DronePathFollower : MonoBehaviour
    {
        [Tooltip("Parent whose children are the waypoints (>= 4)")]
        public Transform pathRoot;
        [Tooltip("< 0 means: use drone.path_speed_mps from simconfig.json")]
        public float speedOverride = -1f;
        [Range(0f, 45f)] public float maxBankDeg = 25f;
        [Tooltip("Phase offset along the loop [0..1] — lets multiple drones share airspace without bunching")]
        [Range(0f, 1f)] public float startOffset = 0f;

        const int SamplesPerSegment = 24;

        Vector3[] _samples;
        float[] _cumLen;
        float _totalLen;
        float _dist;
        float _speed;
        Vector3 _lastVel;
        Vector3 _smoothAccel;

        void Start()
        {
            var cfg = SimConfig.Instance;
            _speed = speedOverride > 0f ? speedOverride : cfg.drone.path_speed_mps;
            transform.localScale = Vector3.one * cfg.drone.scale;
            BuildSpline();
            if (_samples != null)
            {
                _dist = startOffset * _totalLen;
                transform.position = SampleAt(_dist);
            }
        }

        void BuildSpline()
        {
            if (pathRoot == null || pathRoot.childCount < 4)
            {
                Debug.LogError("[Sim] DronePathFollower needs a pathRoot with >= 4 waypoints");
                enabled = false;
                return;
            }

            int n = pathRoot.childCount;
            var pts = new Vector3[n];
            for (int i = 0; i < n; i++) pts[i] = pathRoot.GetChild(i).position;

            var samples = new List<Vector3>(n * SamplesPerSegment + 1);
            for (int seg = 0; seg < n; seg++)
            {
                Vector3 p0 = pts[(seg - 1 + n) % n], p1 = pts[seg], p2 = pts[(seg + 1) % n], p3 = pts[(seg + 2) % n];
                for (int s = 0; s < SamplesPerSegment; s++)
                    samples.Add(CatmullRom(p0, p1, p2, p3, s / (float)SamplesPerSegment));
            }
            samples.Add(samples[0]); // close the loop

            _samples = samples.ToArray();
            _cumLen = new float[_samples.Length];
            _totalLen = 0f;
            for (int i = 1; i < _samples.Length; i++)
            {
                _totalLen += Vector3.Distance(_samples[i - 1], _samples[i]);
                _cumLen[i] = _totalLen;
            }
            Debug.Log($"[Sim] Path: {n} waypoints, {_totalLen:F0} m loop, {_speed} m/s ({_totalLen / _speed:F0}s per lap)");
        }

        static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t, t3 = t2 * t;
            return 0.5f * ((2f * p1) + (-p0 + p2) * t
                   + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                   + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }

        Vector3 SampleAt(float dist)
        {
            dist = Mathf.Repeat(dist, _totalLen);
            int lo = 0, hi = _cumLen.Length - 1;
            while (lo < hi - 1)
            {
                int mid = (lo + hi) / 2;
                if (_cumLen[mid] <= dist) lo = mid; else hi = mid;
            }
            float segLen = _cumLen[hi] - _cumLen[lo];
            float t = segLen > 1e-6f ? (dist - _cumLen[lo]) / segLen : 0f;
            return Vector3.Lerp(_samples[lo], _samples[hi], t);
        }

        void Update()
        {
            if (_samples == null) return;

            _dist += _speed * Time.deltaTime;
            Vector3 pos = SampleAt(_dist);
            Vector3 ahead = SampleAt(_dist + Mathf.Max(_speed * 0.25f, 1f));
            Vector3 vel = (ahead - pos).normalized * _speed;

            transform.position = pos;

            Vector3 flatFwd = new Vector3(vel.x, 0f, vel.z);
            if (flatFwd.sqrMagnitude > 1e-4f)
            {
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                Vector3 accel = (vel - _lastVel) / dt;
                _smoothAccel = Vector3.Lerp(_smoothAccel, accel, 5f * dt);

                Vector3 fwd = flatFwd.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                float bank = Mathf.Clamp(-Vector3.Dot(_smoothAccel, right) * 2f, -maxBankDeg, maxBankDeg);
                float pitch = Mathf.Clamp(Vector3.Dot(_smoothAccel, fwd) * 1.5f, -maxBankDeg, maxBankDeg);
                transform.rotation = Quaternion.LookRotation(fwd, Vector3.up) * Quaternion.Euler(pitch, 0f, bank);
            }
            _lastVel = vel;
        }
    }
}
