using UnityEngine;

namespace DroneSim
{
    /// <summary>
    /// Slowly spins the panoramic skybox so the HDRI's baked clouds drift like wind.
    /// Runs on a runtime clone of the material, so play mode never dirties the
    /// hand-tuned HK_SkyPano.mat on disk. The sun light is left alone on purpose:
    /// it was matched to the photogrammetry's baked shadows, which don't rotate.
    /// </summary>
    public class SkyDrift : MonoBehaviour
    {
        public float degreesPerSecond = 0.4f; // ~0.2 = calm day, 0.6+ = breezy; live-tunable in play mode

        Material _clone;
        float _rotation;

        void Start()
        {
            var src = RenderSettings.skybox;
            // procedural fallback sky has no _Rotation — just go inert
            if (src == null || !src.HasProperty("_Rotation")) { enabled = false; return; }
            _clone = new Material(src);
            RenderSettings.skybox = _clone;
            _rotation = _clone.GetFloat("_Rotation");
        }

        void Update()
        {
            _rotation = Mathf.Repeat(_rotation + degreesPerSecond * Time.deltaTime, 360f);
            _clone.SetFloat("_Rotation", _rotation);
            // no DynamicGI.UpdateEnvironment() here: ambient is near rotation-invariant
            // for this sky and re-baking the environment probe every frame is costly
        }
    }
}
