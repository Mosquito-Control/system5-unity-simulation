using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace DroneSim
{
    /// <summary>
    /// Live, DJI-style ("table side") flight from a RadioMaster Pocket / any HID radio read directly
    /// through the Input System (project is Input-System-only, see ProjectSettings activeInputHandler).
    /// Kinematic like <see cref="DronePathFollower"/> (no Rigidbody): sticks command a TARGET velocity
    /// in the heading frame, velocity is smoothed toward it, so centred sticks brake to a stable hover
    /// that holds position + altitude — self-levelling, no drift, not FPV acro. A keyboard key resets
    /// the drone to its spawn (bottom of the camera-coverage volume, facing north / +Z).
    /// Lives under the "Drones" root so LabelPublisher captures + labels it like any other drone.
    /// </summary>
    public class DroneRcController : MonoBehaviour
    {
        [Header("Spawn (set by HKSceneBuilder from hk_*_setup.json)")]
        public Vector3 spawnPos;
        [Tooltip("Yaw at spawn; 0 = facing north (+Z)")]
        public float spawnYawDeg = 0f;

        [Header("Flight envelope")]
        public float maxHorizSpeed = 12f;   // m/s at full stick
        public float maxClimbRate = 5f;     // m/s at full throttle deflection
        public float maxYawRate = 90f;      // deg/s at full rudder
        public float accel = 20f;           // m/s^2 — how fast velocity chases the stick target
        public float minAltitude = 5f;
        public float maxAltitude = 260f;
        [Range(0f, 45f)] public float maxLeanDeg = 25f; // cosmetic body tilt

        [Header("Channel map (axis indices on the detected device)")]
        public int rollCh = 0;
        public int pitchCh = 1;
        public int thrCh = 2;
        public int yawCh = 3;
        public bool invertRoll = false;
        public bool invertPitch = true;
        public bool invertThr = false;
        public bool invertYaw = false;
        [Range(0f, 0.5f)] public float deadzone = 0.05f;
        [Tooltip("True: throttle axis is centred (-1..1, centre = hold). False: 0..1 (bottom = 0), remapped so mid = hover")]
        public bool throttleCentered = true;

        [Header("Reset")]
        public Key resetKey = Key.R;

        Vector3 _vel;
        float _yawDeg;
        InputDevice _device;
        readonly List<AxisControl> _axes = new List<AxisControl>();
        readonly List<bool> _axis01 = new List<bool>(); // Unity's HID fallback normalizes non-stick axes to 0..1 (centre 0.5)

        void Start()
        {
            transform.localScale = Vector3.one * SimConfig.Instance.drone.scale;
            ResolveDevice();
            Debug.Log(_device != null
                ? $"[Sim] RC: device '{_device.displayName}' with {_axes.Count} axes — map roll={rollCh} pitch={pitchCh} thr={thrCh} yaw={yawCh}"
                : "[Sim] RC: no joystick/gamepad found — keyboard fallback (WASD / QE / Shift+Ctrl), R to reset");
            DoReset();
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[resetKey].wasPressedThisFrame) DoReset();
            if (kb != null && kb.lKey.wasPressedThisFrame && _axes.Count > 0) DumpAxes();

            float roll, pitch, yaw, climb;
            ReadInputs(out roll, out pitch, out yaw, out climb);

            // yaw integrates a turn rate; horizontal velocity target is in the heading frame
            _yawDeg += yaw * maxYawRate * Time.deltaTime;
            Quaternion heading = Quaternion.Euler(0f, _yawDeg, 0f);
            Vector3 target = heading * new Vector3(roll, 0f, pitch) * maxHorizSpeed;
            target.y = climb * maxClimbRate;

            _vel = Vector3.MoveTowards(_vel, target, accel * Time.deltaTime);

            Vector3 pos = transform.position + _vel * Time.deltaTime;
            pos.y = Mathf.Clamp(pos.y, minAltitude, maxAltitude);
            transform.position = pos;

            // cosmetic self-levelling lean: tip into the current horizontal velocity
            Vector3 localVel = Quaternion.Inverse(heading) * _vel;
            float leanPitch = Mathf.Clamp(localVel.z / Mathf.Max(maxHorizSpeed, 0.01f) * maxLeanDeg, -maxLeanDeg, maxLeanDeg);
            float leanBank = Mathf.Clamp(-localVel.x / Mathf.Max(maxHorizSpeed, 0.01f) * maxLeanDeg, -maxLeanDeg, maxLeanDeg);
            transform.rotation = heading * Quaternion.Euler(leanPitch, 0f, leanBank);
        }

        void ReadInputs(out float roll, out float pitch, out float yaw, out float climb)
        {
            if (_device == null || !_device.added) ResolveDevice();

            if (_axes.Count > 0)
            {
                roll = Shape(Axis(rollCh), invertRoll);
                pitch = Shape(Axis(pitchCh), invertPitch);
                yaw = Shape(Axis(yawCh), invertYaw);
                float t = Axis(thrCh);
                if (!throttleCentered) t = t * 2f - 1f; // 0..1 stick -> centred so mid = hover
                climb = Shape(t, invertThr);
                return;
            }

            // keyboard fallback (Input-System): testable with no radio attached
            var kb = Keyboard.current;
            roll = pitch = yaw = climb = 0f;
            if (kb == null) return;
            if (kb.dKey.isPressed) roll += 1f;
            if (kb.aKey.isPressed) roll -= 1f;
            if (kb.wKey.isPressed) pitch += 1f;
            if (kb.sKey.isPressed) pitch -= 1f;
            if (kb.eKey.isPressed) yaw += 1f;
            if (kb.qKey.isPressed) yaw -= 1f;
            if (kb.leftShiftKey.isPressed) climb += 1f;
            if (kb.leftCtrlKey.isPressed) climb -= 1f;
        }

        float Shape(float v, bool invert)
        {
            if (Mathf.Abs(v) < deadzone) return 0f;
            // rescale past the deadzone so the usable range still reaches +-1
            float s = (Mathf.Abs(v) - deadzone) / (1f - deadzone) * Mathf.Sign(v);
            return Mathf.Clamp(invert ? -s : s, -1f, 1f);
        }

        float Axis(int idx)
        {
            if (idx < 0 || idx >= _axes.Count) return 0f;
            float v = _axes[idx].ReadValue();
            // re-centre 0..1 HID axes to the signed -1..1 the flight maths expects
            // (browser Gamepad API shows these signed; Unity does not — see remote.jpeg survey)
            return _axis01[idx] ? v * 2f - 1f : v;
        }

        void ResolveDevice()
        {
            InputDevice dev = Joystick.current;
            if (dev == null)
            {
                foreach (var d in InputSystem.devices)
                    if (d is Joystick || d is Gamepad) { dev = d; break; }
            }
            if (dev == _device && _axes.Count > 0) return;

            _device = dev;
            _axes.Clear();
            _axis01.Clear();
            if (_device == null) return;
            // leaf analog axes only: AxisControl covers stick x/y, twist, throttle, sliders;
            // ButtonControl derives from AxisControl, so exclude it (digital, not a channel)
            foreach (var c in _device.allControls)
                if (c is AxisControl ac && !(c is ButtonControl))
                {
                    _axes.Add(ac);
                    // stick x/y come pre-normalized to -1..1; every other HID axis
                    // (z, rx, sliders — i.e. EdgeTX throttle + rudder) arrives 0..1
                    _axis01.Add(!(c.parent is StickControl));
                }
        }

        /// One-line raw vs mapped dump (L key) — verify the channel map on any machine via the log.
        void DumpAxes()
        {
            var parts = new List<string>();
            for (int i = 0; i < _axes.Count; i++)
                parts.Add($"a{i}={_axes[i].ReadValue():F2}{(_axis01[i] ? "(01)" : "")}->{Axis(i):F2}");
            Debug.Log($"[Sim] RC axes: {string.Join(" ", parts)}");
        }

        void DoReset()
        {
            _vel = Vector3.zero;
            _yawDeg = spawnYawDeg;
            transform.position = spawnPos;
            transform.rotation = Quaternion.Euler(0f, spawnYawDeg, 0f);
        }
    }
}
