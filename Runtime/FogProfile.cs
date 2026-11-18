using UnityEngine;

namespace DriftFog
{
    /// <summary>視線に沿った積分の最大サンプル数を選ぶ。</summary>
    public enum FogQuality
    {
        /// <summary>12 ステップ。</summary>
        Low = 12,

        /// <summary>24 ステップ。</summary>
        Balanced = 24,

        /// <summary>48 ステップ。</summary>
        High = 48,
    }

    /// <summary>霧の体積領域の外形。</summary>
    public enum FogShape
    {
        /// <summary>柔らかい境界を持つ直方体。</summary>
        Box,

        /// <summary>配置した直方体に内接する楕円体。</summary>
        Ellipsoid,
    }

    /// <summary>複数の体積で共有できる霧の光学特性、流れと描画品質。</summary>
    [CreateAssetMenu(menuName = "Drift Fog/Fog Profile", fileName = "Fog Profile")]
    public sealed class FogProfile : ScriptableObject
    {
        [Header("Scattering")]
        [SerializeField] private Color _tint = new Color(0.68f, 0.83f, 0.92f);
        [SerializeField] private Color _shade = new Color(0.25f, 0.37f, 0.44f);
        [SerializeField, Min(0f)] private float _density = 0.38f;

        [Header("Flow")]
        [SerializeField] private Vector3 _wind = new Vector3(0.45f, 0f, 0.14f);
        [Tooltip("Repeats of the 3D noise texture per world metre.")]
        [SerializeField, Min(0.001f)] private float _noiseFrequency = 0.065f;
        [SerializeField, Range(0f, 1f)] private float _turbulence = 0.62f;
        [SerializeField, Range(0f, 1f)] private float _heightFalloff = 0.72f;
        [SerializeField, Range(0.01f, 0.5f)] private float _edgeSoftness = 0.14f;

        [Header("Fluid Simulation")]
        [Tooltip("Simulate local air velocity and transport density on a 3D GPU grid.")]
        [SerializeField] private bool _simulateFluid = true;
        [Tooltip("Rotational forcing and preservation of small swirls.")]
        [SerializeField, Range(0f, 4f)] private float _vorticity = 1.4f;
        [Tooltip("Strength of alternating vortex shedding at resolved obstacles and moving bodies. Zero disables this forcing.")]
        [SerializeField, Range(0f, 2f)] private float _wakeShedding = 1f;
        [Tooltip("Kinematic viscosity in square metres per second. Diffuses momentum only. Zero skips the diffusion pass.")]
        [SerializeField, Range(0f, 0.08f)] private float _kinematicViscosity;
        [Tooltip("Longest grid axis at High quality. The other two axes preserve the volume proportions.")]
        [SerializeField, Range(88, 128)] private int _highGridResolution = 88;
        [Tooltip("Rate per second at which smoke relaxes toward the supplied fog banks.")]
        [SerializeField, Range(0.01f, 2f)] private float _recovery = 0.19f;
        [Tooltip("Fixed simulation ticks per second, independent of the rendering frame rate.")]
        [SerializeField, Range(10, 60)] private int _simulationRate = 30;

        [Header("Rendering")]
        [SerializeField] private FogQuality _quality = FogQuality.Balanced;
        [Tooltip("Maximum ray distance from the camera in world metres.")]
        [SerializeField, Min(1f)] private float _maxDistance = 100f;

        /// <summary>光が当たる霧の色。</summary>
        public Color Tint
        {
            get => _tint; set => _tint = value;
        }

        /// <summary>陰になる霧の色。</summary>
        public Color Shade
        {
            get => _shade; set => _shade = value;
        }

        /// <summary>単位距離当たりの光の減衰。</summary>
        public float Density
        {
            get => Mathf.Max(0f, _density); set => _density = value;
        }

        /// <summary>ワールド座標での風速（メートル毎秒）。</summary>
        public Vector3 Wind
        {
            get => _wind; set => _wind = value;
        }

        /// <summary>空間ノイズが 1 メートル当たりに繰り返す回数。</summary>
        public float NoiseFrequency
        {
            get => Mathf.Max(0.001f, _noiseFrequency); set => _noiseFrequency = value;
        }

        /// <summary>霧の渦と細部の強さ。</summary>
        public float Turbulence
        {
            get => Mathf.Clamp01(_turbulence); set => _turbulence = value;
        }

        /// <summary>上に行くほど密度を減らす強さ。</summary>
        public float HeightFalloff
        {
            get => Mathf.Clamp01(_heightFalloff); set => _heightFalloff = value;
        }

        /// <summary>境界をぼかす体積寸法に対する割合。</summary>
        public float EdgeSoftness
        {
            get => Mathf.Clamp(_edgeSoftness, 0.01f, 0.5f); set => _edgeSoftness = value;
        }

        /// <summary>視線方向のサンプリング品質。</summary>
        public FogQuality Quality
        {
            get => _quality; set => _quality = value;
        }

        /// <summary>カメラから積分を打ち切る距離（メートル）。</summary>
        public float MaxDistance
        {
            get => Mathf.Max(1f, _maxDistance); set => _maxDistance = value;
        }

        /// <summary>対応する GPU で速度・煙密度の三次元格子を使用する。</summary>
        public bool SimulateFluid
        {
            get => _simulateFluid; set => _simulateFluid = value;
        }

        /// <summary>渦の細部を維持する回転の強さ。</summary>
        public float Vorticity
        {
            get => Mathf.Clamp(_vorticity, 0f, 4f); set => _vorticity = value;
        }

        /// <summary>風を受ける障害物の後流に交互の渦を供給する強度。ゼロで剥離の補助を無効にする。</summary>
        public float WakeShedding
        {
            get => Mathf.Clamp(_wakeShedding, 0f, 2f); set => _wakeShedding = value;
        }

        /// <summary>速度を拡散する動粘性係数（m²/s）。ゼロで粘性計算を省略します。</summary>
        public float KinematicViscosity
        {
            get => float.IsFinite(_kinematicViscosity) ? Mathf.Clamp(_kinematicViscosity, 0f, 0.08f) : 0f;
            set => _kinematicViscosity = value;
        }

        /// <summary>High 格子の長辺セル数。4 セル単位で解像度を選びます。</summary>
        public int HighGridResolution
        {
            get => Mathf.RoundToInt(Mathf.Clamp(_highGridResolution, 88, 128) * 0.25f) * 4;
            set => _highGridResolution = value;
        }

        /// <summary>霧を環境の供給密度へ戻す速度（毎秒）。</summary>
        public float Recovery
        {
            get => Mathf.Clamp(_recovery, 0.01f, 2f); set => _recovery = value;
        }

        /// <summary>格子を進める目標更新回数（毎秒）。</summary>
        public int SimulationRate
        {
            get => Mathf.Clamp(_simulationRate, 10, 60); set => _simulationRate = value;
        }
    }
}
