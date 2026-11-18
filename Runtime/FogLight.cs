using System.Collections.Generic;
using UnityEngine;

namespace DriftFog
{
    /// <summary>ポイントライトの位置・色・範囲を、重なる霧の散乱光に反映する。</summary>
    [ExecuteAlways]
    [RequireComponent(typeof(Light))]
    [DisallowMultipleComponent]
    [AddComponentMenu("Drift Fog/Fog Light")]
    public sealed class FogLight : MonoBehaviour
    {
        private static readonly List<FogLight> Active = new List<FogLight>();
        [SerializeField, Min(0f)] private float _scattering = 1f;
        private Light _light;

        /// <summary>霧に加算する散乱光の強度倍率。</summary>
        public float Scattering
        {
            get => Mathf.Max(0f, _scattering); set => _scattering = value;
        }

        /// <summary>重なっているポイントライトを描画候補へ渡す。</summary>
        /// <param name="volume">光の候補を選ぶ霧の体積。</param>
        internal static void Collect(DriftFogVolume volume)
        {
            for (int i = 0; i < Active.Count; ++i)
            {
                FogLight source = Active[i];
                if (source == null || !volume.SharesEnvironment(source.gameObject))
                {
                    continue;
                }

                Light lamp = source._light;
                if (!lamp || !lamp.isActiveAndEnabled || lamp.type != LightType.Point || lamp.range <= 0f)
                {
                    continue;
                }

                Color color = QualitySettings.activeColorSpace == ColorSpace.Linear ? lamp.color.linear : lamp.color;
                volume.ConsiderLight(lamp.transform.position, lamp.range, color * (lamp.intensity * source.Scattering));
            }
        }

        private void OnEnable()
        {
            _light = GetComponent<Light>();
            Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }
    }
}
