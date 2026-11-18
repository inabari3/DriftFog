using UnityEngine;
using UnityEngine.Rendering;

namespace DriftFog
{
    /// <summary>Transform の向きと大きさで配置する URP 用の局所体積霧。</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("Drift Fog/Drift Fog Volume")]
    [DefaultExecutionOrder(200)]
    public sealed partial class DriftFogVolume : MonoBehaviour
    {
        private const int SourceLimit = 12;
        private const int LampLimit = 2;
        private static readonly int TintId = Shader.PropertyToID("_FogTint");
        private static readonly int ShadeId = Shader.PropertyToID("_FogShade");
        private static readonly int ShapeId = Shader.PropertyToID("_FogShape");
        private static readonly int MotionId = Shader.PropertyToID("_FogMotion");
        private static readonly int MarchId = Shader.PropertyToID("_FogMarch");
        private static readonly int CountId = Shader.PropertyToID("_FogSourceCount");
        private static readonly int PointsId = Shader.PropertyToID("_FogSources");
        private static readonly int StrengthId = Shader.PropertyToID("_FogStrengths");
        private static readonly int LampsId = Shader.PropertyToID("_FogLamps");
        private static readonly int LampCountId = Shader.PropertyToID("_FogLampCount");
        private static readonly int LampColorsId = Shader.PropertyToID("_FogLampColors");
        private static readonly int FluidActiveId = Shader.PropertyToID("_FogFluidActive");
        private static readonly int FluidDensityId = Shader.PropertyToID("_FogFluidDensity");
        private static readonly int FluidVelocityId = Shader.PropertyToID("_FogFluidVelocity");
        private static readonly int FluidDisplacementId = Shader.PropertyToID("_FogFluidDisplacement");
        private static readonly int FluidDriftId = Shader.PropertyToID("_FogFluidDrift");

        [SerializeField] private FogProfile _profile;
        [SerializeField] private FogShape _shape;

        private readonly Vector4[] _sources = new Vector4[SourceLimit];
        private readonly float[] _strengths = new float[SourceLimit];
        private readonly float[] _sourceScores = new float[SourceLimit];
        private readonly Vector4[] _lamps = new Vector4[LampLimit];
        private readonly Vector4[] _lampColors = new Vector4[LampLimit];
        private readonly float[] _lampScores = new float[LampLimit];
        private int _sourceCount;
        private int _lampCount;
        private Bounds _bounds;
        private MeshRenderer _renderer;
        private MaterialPropertyBlock _block;
        private FogProfile _fallback;
        private FogFluidSimulation _fluid;
        private bool _fluidActive;
        private int _lastSimulationFrame = -1;
        private int _lastRefreshFrame = -1;
        private int _manualFrame = -1;
        private float _lastVisibleTime;
        private FogQuality _allocatedQuality;

        /// <summary>描画に使用するプロファイル。未指定なら付属の Ground Mist を使用する。</summary>
        public FogProfile Profile
        {
            get => _profile ? _profile : _fallback;
            set
            {
                _profile = value;
                if (isActiveAndEnabled)
                {
                    Refresh();
                }
            }
        }

        /// <summary>体積の形状。Transform のローカル軸に沿って配置する。</summary>
        public FogShape Shape
        {
            get => _shape; set => _shape = value;
        }

        /// <summary>流体の正規化密度を持つ三次元格子。未確保時は null。</summary>
        public RenderTexture FluidDensity => _fluid?.Density;

        /// <summary>XYZ に局所座標の面速度（m/s）、W にセル中心の霧の出発層（0～1）を格納する格子。</summary>
        public RenderTexture FluidVelocity => _fluid?.Velocity;

        /// <summary>High のセル中心における背景風相対の局所変位（XYZ、m）。使用できる固定刻みがなければ null。</summary>
        public RenderTexture FluidDisplacement => _fluid?.Displacement;

        /// <summary>局所背景風の積算変位（XYZ、m）と細部変位の有効フラグ（W）。</summary>
        public Vector4 FluidDetailDrift => _fluid?.DetailDrift ?? Vector4.zero;

        /// <summary>障害物が占有するセルを 1 で表す格子。</summary>
        public RenderTexture FluidSolids => _fluid?.Solids;

        /// <summary>直近の圧力射影前の発散を記録した格子。</summary>
        public RenderTexture FluidDivergence => _fluid?.Divergence;

        /// <summary>体積の各軸に沿ったセルの幅（メートル）。</summary>
        public Vector3 FluidCellSize => _fluid?.CellSize ?? Vector3.zero;

        /// <summary>編集時も含め、自動更新の予算と独立して明示した経過時間を流体に与えます。ゼロなら未生成の格子を初期化します。</summary>
        /// <param name="deltaTime">秒単位の経過時間。一度に追従するのは最大二刻みです。</param>
        /// <returns>流体格子を描画できる場合は真。</returns>
        public bool AdvanceSimulation(float deltaTime)
        {
            if (!isActiveAndEnabled || !_renderer || _block == null || !Profile || !Profile.SimulateFluid)
            {
                ReleaseFluid();
                return false;
            }

            _manualFrame = Time.frameCount;
            StepSimulation(deltaTime);
            Refresh();
            return _fluidActive;
        }

        private void StepSimulation(float deltaTime)
        {
            _lastVisibleTime = Time.realtimeSinceStartup;
            _lastSimulationFrame = Time.frameCount;
            _fluid ??= new FogFluidSimulation();
            bool wasActive = _fluidActive;
            _fluidActive = _fluid.Advance(this, _renderer.bounds, deltaTime);
            _allocatedQuality = Profile.Quality;
            _block.SetFloat(FluidActiveId, _fluidActive ? 1f : 0f);
            _block.SetVector(FluidDriftId, FluidDetailDrift);
            if (_fluidActive)
            {
                _block.SetTexture(FluidDensityId, FluidDensity);
                _block.SetTexture(FluidVelocityId, FluidVelocity);
                if (FluidDisplacement)
                {
                    _block.SetTexture(FluidDisplacementId, FluidDisplacement);
                }
            }
            else
            {
                // 使用できない格子が後続の体積の自動更新枠を占有し続けるのを防ぐ。
                _retryBudgetTime = Time.realtimeSinceStartup + 5f;
                if (wasActive)
                {
                    _lastRefreshFrame = -1;
                }
            }
        }

        /// <summary>プロファイルと影響源の現在値を、この体積の描画パラメーターに反映する。</summary>
        public void Refresh()
        {
            FogProfile profile = Profile;
            if (!_renderer || !profile || _block == null)
            {
                return;
            }

            if (_fluid != null && (!profile.SimulateFluid || _allocatedQuality != profile.Quality))
            {
                ReleaseFluid();
            }

            float now = Application.IsPlaying(gameObject) ? Time.time : Time.realtimeSinceStartup;
            _sourceCount = 0;
            _lampCount = 0;
            _bounds = _renderer.bounds;
            if (!_fluidActive)
            {
                FogInteractor.Collect(this, now);
                FogObstacle.Apply(this, _bounds, _block);
            }

            FogLight.Collect(this);

            Vector3 wind = profile.Wind;
            _block.SetColor(TintId, profile.Tint);
            _block.SetColor(ShadeId, profile.Shade);
            _block.SetVector(ShapeId, new Vector4(profile.Density, profile.NoiseFrequency, profile.Turbulence, profile.EdgeSoftness));
            _block.SetVector(MotionId, new Vector4(wind.x, wind.y, wind.z, profile.HeightFalloff));
            _block.SetVector(MarchId, new Vector4((int)profile.Quality, profile.MaxDistance, (int)_shape, now));
            _block.SetFloat(CountId, _sourceCount);
            if (_sourceCount > 0)
            {
                _block.SetVectorArray(PointsId, _sources);
                _block.SetFloatArray(StrengthId, _strengths);
            }

            _block.SetFloat(LampCountId, _lampCount);
            if (_lampCount > 0)
            {
                _block.SetVectorArray(LampsId, _lamps);
                _block.SetVectorArray(LampColorsId, _lampColors);
            }

            _renderer.SetPropertyBlock(_block);
            _lastRefreshFrame = Time.frameCount;
        }

        /// <summary>作用点を重なりで除外し、上限を超えた場合は有効な候補を優先する。</summary>
        /// <param name="position">作用点のワールド位置。</param>
        /// <param name="radius">作用半径。</param>
        /// <param name="strength">押し出す割合。</param>
        /// <param name="live">現在の影響源なら真、航跡なら偽。</param>
        internal void ConsiderSource(Vector3 position, float radius, float strength, bool live)
        {
            if (_bounds.SqrDistance(position) > radius * radius * 2.25f)
            {
                return;
            }

            float score = (live ? 2f : 0f) + strength + 0.001f / (1f + (position - _bounds.center).sqrMagnitude);
            int index = GetSlot(_sourceScores, ref _sourceCount, score);
            if (index >= 0)
            {
                _sources[index] = new Vector4(position.x, position.y, position.z, radius);
                _strengths[index] = strength;
            }
        }

        /// <summary>体積に届く光のうち、中心への寄与が大きい光を候補にする。</summary>
        /// <param name="position">光源のワールド位置。</param>
        /// <param name="range">光が届く半径。</param>
        /// <param name="radiance">霧の散乱に使う色と強度。</param>
        internal void ConsiderLight(Vector3 position, float range, Color radiance)
        {
            if (_bounds.SqrDistance(position) >= range * range || radiance.maxColorComponent <= 0f)
            {
                return;
            }

            float score = radiance.maxColorComponent / (1f + (position - _bounds.center).sqrMagnitude / (range * range));
            int index = GetSlot(_lampScores, ref _lampCount, score);
            if (index >= 0)
            {
                _lamps[index] = new Vector4(position.x, position.y, position.z, range);
                _lampColors[index] = radiance;
            }
        }

        /// <summary>プレイ中は加算ロードしたシーンを連携し、編集時はプレビューを分離する。</summary>
        /// <param name="source">影響を与えるオブジェクト。</param>
        /// <returns>同じ描画環境に属する場合は真。</returns>
        internal bool SharesEnvironment(GameObject source)
        {
            return source.scene == gameObject.scene || (Application.IsPlaying(gameObject) && Application.IsPlaying(source));
        }

        private static int GetSlot(float[] scores, ref int count, float score)
        {
            int index;
            if (count < scores.Length)
            {
                index = count++;
            }
            else
            {
                index = 0;
                for (int i = 1; i < count; ++i)
                {
                    if (scores[i] < scores[index])
                    {
                        index = i;
                    }
                }

                if (score <= scores[index])
                {
                    return -1;
                }
            }

            scores[index] = score;
            return index;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<MeshRenderer>();
            _block ??= new MaterialPropertyBlock();
            _fallback = Resources.Load<FogProfile>("DriftFog/GroundMist");
            GetComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            _renderer.sharedMaterial = Resources.Load<Material>("DriftFog/VolumeMaterial");
            _renderer.shadowCastingMode = ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            _renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            _renderer.enabled = true;
            RegisterVolume();
            Refresh();
        }

        private void LateUpdate()
        {
            if (_fluid != null && (Time.realtimeSinceStartup - _lastVisibleTime > 4f || !Profile || !Profile.SimulateFluid || _allocatedQuality != Profile.Quality))
            {
                ReleaseFluid();
            }
        }

        private void ReleaseFluid()
        {
            if (_fluid == null)
            {
                return;
            }

            _fluid.Dispose();
            _fluid = null;
            _fluidActive = false;
            _lastRefreshFrame = -1;
            _lastSimulationFrame = -1;
            _manualFrame = -1;
            _block.SetFloat(FluidActiveId, 0f);
            _block.SetVector(FluidDriftId, Vector4.zero);
            if (_renderer)
            {
                _renderer.SetPropertyBlock(_block);
            }
        }

        private void OnDisable()
        {
            UnregisterVolume();
            ReleaseFluid();
            if (_renderer)
            {
                _renderer.enabled = false;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.4f, 0.9f, 0.95f, 0.8f);
            if (_shape == FogShape.Ellipsoid)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
            }
            else
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            }

            Gizmos.matrix = previous;
        }
    }
}
