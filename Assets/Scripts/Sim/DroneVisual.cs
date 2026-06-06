using UnityEngine;

namespace DroneSim
{
    /// <summary>Spins the rotor discs (alternating directions). Pure cosmetics.</summary>
    public class DroneVisual : MonoBehaviour
    {
        public Transform[] rotors;
        public float spinDegPerSec = 3000f;

        void Update()
        {
            if (rotors == null) return;
            for (int i = 0; i < rotors.Length; i++)
            {
                if (rotors[i] == null) continue;
                float dir = (i % 2 == 0) ? 1f : -1f;
                rotors[i].Rotate(0f, dir * spinDegPerSec * Time.deltaTime, 0f, Space.Self);
            }
        }
    }
}
