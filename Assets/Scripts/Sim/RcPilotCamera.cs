using UnityEngine;
using UnityEngine.InputSystem;

namespace DroneSim
{
    /// <summary>
    /// Pilot view for the RC drone, driven on the spectator (screen) camera. V cycles
    /// Chase (default, fly-behind) -> Onboard (body-mounted, gimbal-levelled like a DJI feed)
    /// -> Spectator (the original city-overview pose, captured at Play start).
    /// Wired onto SpectatorCamera by HKSceneBuilder.ApplyPath when rc.enabled.
    /// </summary>
    public class RcPilotCamera : MonoBehaviour
    {
        [Tooltip("Drone_RC (set by HKSceneBuilder)")]
        public Transform target;
        public Key cycleKey = Key.V;

        [Header("Chase")]
        public Vector3 chaseOffset = new Vector3(0f, 5f, -12f); // heading-frame: above + behind
        public float chaseLag = 4f;                             // higher = snappier follow

        [Header("Onboard (gimbal)")]
        public Vector3 onboardOffset = new Vector3(0f, 0.2f, 1.0f); // heading-frame metres from body centre
        public float onboardPitchDeg = 12f;                         // fixed tilt down, like a cruising DJI gimbal

        public enum ViewMode { Chase, Onboard, Spectator }
        public ViewMode mode = ViewMode.Chase;

        Vector3 _specPos;
        Quaternion _specRot;

        void Start()
        {
            _specPos = transform.position;
            _specRot = transform.rotation;
            SnapToMode();
        }

        void LateUpdate()
        {
            var kb = Keyboard.current;
            if (kb != null && kb[cycleKey].wasPressedThisFrame)
            {
                mode = (ViewMode)(((int)mode + 1) % 3);
                SnapToMode();
            }
            if (target == null) return;

            // yaw-only heading: ignore the cosmetic body lean so the horizon stays level
            Quaternion heading = Quaternion.Euler(0f, target.eulerAngles.y, 0f);

            switch (mode)
            {
                case ViewMode.Chase:
                {
                    Vector3 want = target.position + heading * chaseOffset;
                    float k = 1f - Mathf.Exp(-chaseLag * Time.deltaTime); // framerate-independent smoothing
                    transform.position = Vector3.Lerp(transform.position, want, k);
                    Quaternion look = Quaternion.LookRotation(
                        (target.position + heading * (Vector3.forward * 8f)) - transform.position, Vector3.up);
                    transform.rotation = Quaternion.Slerp(transform.rotation, look, k);
                    break;
                }
                case ViewMode.Onboard:
                    transform.position = target.position + heading * onboardOffset;
                    transform.rotation = heading * Quaternion.Euler(onboardPitchDeg, 0f, 0f);
                    break;
                case ViewMode.Spectator:
                    break; // parked at the captured overview pose
            }
        }

        void SnapToMode()
        {
            if (mode == ViewMode.Spectator)
            {
                transform.SetPositionAndRotation(_specPos, _specRot);
            }
            else if (mode == ViewMode.Onboard && target != null)
            {
                Quaternion heading = Quaternion.Euler(0f, target.eulerAngles.y, 0f);
                transform.SetPositionAndRotation(target.position + heading * onboardOffset,
                    heading * Quaternion.Euler(onboardPitchDeg, 0f, 0f));
            }
            // Chase eases in from wherever the camera currently is
        }
    }
}
