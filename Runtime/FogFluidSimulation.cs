using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace DriftFog
{
    /// <summary>一つの霧ボリュームに対応する三次元格子と固定時間刻みの GPU 計算を管理します。</summary>
    internal sealed class FogFluidSimulation : IDisposable
    {
        private const int ActorLimit = 12;
        private const int BoxLimit = 8;
        private const int FirstPressure = 6;
        private const int SecondPressure = 7;
        private const int CoarseStencil = 10;
        private const int FirstCoarsePressure = 11;
        private const int SecondCoarsePressure = 12;
        private const int FirstDisplacement = 13;
        private const int SecondDisplacement = 14;
        // 角柱の後流に使う代表ストローハル数。風上から見た幅で無次元化する。
        private const float BoxStrouhal = 0.13f;
        private const float ActorStrouhal = 0.21f;
        private static readonly int ActorShedding = Shader.PropertyToID("_ActorShedding");
        private static readonly int WakeCount = Shader.PropertyToID("_WakeCount");
        private static readonly int WakeCenters = Shader.PropertyToID("_WakeCenters");
        private static readonly int WakeAxes = Shader.PropertyToID("_WakeAxes");
        private static readonly int WakeBounds = Shader.PropertyToID("_WakeBounds");
        private static readonly int PressureRead = Shader.PropertyToID("_PressureRead");
        private static readonly int PressureWrite = Shader.PropertyToID("_PressureWrite");
        private static readonly int DensityRead = Shader.PropertyToID("_DensityRead");
        private static readonly int DensityWrite = Shader.PropertyToID("_DensityWrite");
        private static readonly int CoarsePressureRead = Shader.PropertyToID("_CoarsePressureRead");
        private static readonly int CoarsePressureWrite = Shader.PropertyToID("_CoarsePressureWrite");
        private static readonly int DisplacementRead = Shader.PropertyToID("_DisplacementRead");
        private static readonly int DisplacementWrite = Shader.PropertyToID("_DisplacementWrite");
        private static readonly int DisplacementHasHistory = Shader.PropertyToID("_DisplacementHasHistory");
        private readonly RenderTexture[] _textures = new RenderTexture[15];
        private readonly FogInteractor[] _actors = new FogInteractor[ActorLimit];
        private readonly FogInteractor[] _previousActors = new FogInteractor[ActorLimit];
        private readonly Vector3[] _previousPositions = new Vector3[ActorLimit];
        private readonly float[] _previousActorPhases = new float[ActorLimit];
        private readonly float[] _actorPhases = new float[ActorLimit];
        private readonly Vector4[] _actorShedding = new Vector4[ActorLimit];
        private readonly Vector4[] _actorPositions = new Vector4[ActorLimit];
        private readonly Vector4[] _actorVelocities = new Vector4[ActorLimit];
        private readonly float[] _actorDistances = new float[ActorLimit];
        private readonly Matrix4x4[] _boxes = new Matrix4x4[BoxLimit];
        private readonly Matrix4x4[] _previousBoxes = new Matrix4x4[BoxLimit];
        private readonly float[] _boxDistances = new float[BoxLimit];
        private readonly FogObstacle[] _obstacles = new FogObstacle[BoxLimit];
        private readonly Vector4[] _wakeCenters = new Vector4[BoxLimit];
        private readonly Vector4[] _wakeAxes = new Vector4[BoxLimit];
        private readonly Vector4[] _wakeBounds = new Vector4[BoxLimit];
        private WakeSource[] _wakeSources = new WakeSource[BoxLimit];
        private WakeSource[] _oldWakeSources = new WakeSource[BoxLimit];
        private int _trackedBoxCount;
        private bool _skipBoxMotion;
        private ComputeShader _compute;
        private Vector3Int _grid;
        private Vector3 _dimensions;
        private FogQuality _quality;
        private Matrix4x4 _domainToWorld;
        private Matrix4x4 _worldToDomain;
        private int _raster;
        private int _initialize;
        private int _clearPressure;
        private int _curl;
        private int _advect;
        private int _predictVelocity;
        private int _diffuseVelocity;
        private int _diffusionSteps;
        private int _projectionInput;
        private int _resolution;
        private int _divergence;
        private int _jacobi;
        private int _restrictPressure;
        private int _solveCoarse;
        private int _prolongatePressure;
        private int _project;
        private int _transportDisplacement = -1;
        private int _displacementIndex = FirstDisplacement;
        private bool _hasDisplacement;
        private Vector3 _domainWind;
        private double _driftX;
        private double _driftY;
        private double _driftZ;
        private int _predict;
        private int _correct;
        private int _densityIndex;
        private int _coarseIterations;
        private int _postIterations;
        private int _actorCount;
        private int _previousActorCount;
        private int _boxCount;
        private int _previousBoxCount;
        private float _clock;
        private float _accumulator;
        private float _sinceSample;
        private float _retryTime;

        /// <summary>現在の密度格子。未初期化時は null。</summary>
        internal RenderTexture Density => _textures[_densityIndex];

        /// <summary>圧力射影後の面速度格子。</summary>
        internal RenderTexture Velocity => _textures[0];

        /// <summary>High の背景風に対する局所変位（m）。最初の固定刻みが完了するまで null。</summary>
        internal RenderTexture Displacement => _hasDisplacement ? _textures[_displacementIndex] : null;

        /// <summary>固定刻みで積算した背景風の局所変位（m）と、細部格子の有効フラグ。</summary>
        internal Vector4 DetailDrift => _hasDisplacement ? new Vector4((float)_driftX, (float)_driftY, (float)_driftZ, 1f) : Vector4.zero;

        /// <summary>障害物の占有格子。</summary>
        internal RenderTexture Solids => _textures[9];

        /// <summary>直近の圧力射影直前に測った発散。</summary>
        internal RenderTexture Divergence => _textures[8];

        /// <summary>各軸のセル間隔をメートルで返します。</summary>
        internal Vector3 CellSize
        {
            get; private set;
        }

        /// <summary>格子を必要時に生成して固定刻みで進めます。</summary>
        /// <param name="volume">格子の配置とプロファイル。</param>
        /// <param name="bounds">ワールド空間の計算範囲。</param>
        /// <param name="deltaTime">前回の呼び出しからの秒数。</param>
        /// <returns>描画に使える格子がある場合は真。</returns>
        internal bool Advance(DriftFogVolume volume, Bounds bounds, float deltaTime)
        {
            FogProfile profile = volume.Profile;
            Matrix4x4 local = volume.transform.localToWorldMatrix;
            Vector3 dimensions = new Vector3(local.MultiplyVector(Vector3.right).magnitude, local.MultiplyVector(Vector3.up).magnitude, local.MultiplyVector(Vector3.forward).magnitude);
            if (Mathf.Min(dimensions.x, Mathf.Min(dimensions.y, dimensions.z)) < 0.001f || !SystemInfo.supportsComputeShaders || !SystemInfo.supports3DRenderTextures)
            {
                Dispose();
                return false;
            }

            Vector3 inverse = new Vector3(1f / dimensions.x, 1f / dimensions.y, 1f / dimensions.z);
            _domainToWorld = local * Matrix4x4.TRS(-0.5f * Vector3.one, Quaternion.identity, inverse);
            _worldToDomain = _domainToWorld.inverse;
            int resolution = profile.Quality == FogQuality.Low ? 40 : (profile.Quality == FogQuality.High ? profile.HighGridResolution : 64);
            bool initialize = !_compute || _quality != profile.Quality || _resolution != resolution || (dimensions - _dimensions).sqrMagnitude > 0.000001f || !TexturesValid();
            if (initialize && Time.realtimeSinceStartup < _retryTime)
            {
                return false;
            }

            try
            {
                if (initialize)
                {
                    Dispose();
                    if (!Allocate(dimensions, profile.Quality, resolution))
                    {
                        Dispose();
                        _retryTime = Time.realtimeSinceStartup + 5f;
                        return false;
                    }
                }

                float step = 1f / Mathf.Clamp(profile.SimulationRate, 10, 60);
                float elapsed = float.IsFinite(deltaTime) ? Mathf.Max(0f, deltaTime) : 0f;
                _sinceSample += elapsed;
                _accumulator = Mathf.Min(_accumulator + elapsed, 2f * step);
                int count = Mathf.Min(2, Mathf.FloorToInt((_accumulator + 0.000001f) / step));
                if (!initialize && count == 0)
                {
                    return true;
                }

                _actorCount = 0;
                _boxCount = 0;
                FogInteractor.CollectFluid(volume, this, bounds);
                FogObstacle.CollectFluid(volume, this, bounds);
                SetParameters(profile, step);
                UpdateSolids();
                if (initialize)
                {
                    Dispatch(_initialize);
                    _compute.SetTexture(_clearPressure, PressureWrite, Divergence);
                    Dispatch(_clearPressure);
                    _compute.SetTexture(_clearPressure, PressureWrite, _textures[FirstPressure]);
                }

                for (int i = 0; i < count; ++i)
                {
                    _clock += step;
                    Step(profile.Vorticity, profile.WakeShedding, step);
                }

                _accumulator = Mathf.Max(0f, _accumulator - count * step);
                _sinceSample = 0f;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Drift Fog の流体格子が使用できないため軽量描画に切り替えます。 " + exception.Message, volume);
                Dispose();
                _retryTime = Time.realtimeSinceStartup + 5f;
                return false;
            }
        }

        /// <summary>休止中の移動量が再開した一刻みに集中しないよう、前回の速度測定を捨てます。</summary>
        internal void ResetActorHistory()
        {
            _previousActorCount = 0;
            _sinceSample = 0f;
            _skipBoxMotion = true;
        }

        /// <summary>体積内の移動体を候補に加えます。</summary>
        /// <param name="actor">速度の追跡対象。</param>
        /// <param name="world">世界座標の中心。</param>
        /// <param name="radius">影響半径（メートル）。</param>
        /// <param name="strength">影響の強さ。</param>
        internal void AddActor(FogInteractor actor, Vector3 world, float radius, float strength)
        {
            Vector3 position = _worldToDomain.MultiplyPoint3x4(world);
            int index = NearestSlot(_actorDistances, ref _actorCount, (position - _dimensions * 0.5f).sqrMagnitude);
            if (index < 0)
            {
                return;
            }

            Vector3 velocity = Vector3.zero;
            float phase = 0f;
            for (int i = 0; i < _previousActorCount; ++i)
            {
                if (_previousActors[i] == actor && _sinceSample > 0.0001f)
                {
                    Vector3 displacement = position - _previousPositions[i];
                    if (displacement.sqrMagnitude < radius * radius * 64f)
                    {
                        velocity = Vector3.ClampMagnitude(displacement / _sinceSample, 24f);
                        phase = _previousActorPhases[i];
                    }

                    break;
                }
            }

            _actors[index] = actor;
            _actorPhases[index] = phase;
            _actorPositions[index] = new Vector4(position.x, position.y, position.z, radius);
            _actorVelocities[index] = new Vector4(velocity.x, velocity.y, velocity.z, strength);
        }

        /// <summary>世界から単位箱への変換を、格子座標からの変換に合成します。</summary>
        /// <param name="obstacle">位置と渦の離脱位相を追跡する障害物。</param>
        /// <param name="worldToBox">世界座標から箱の中心・単位寸法への変換。</param>
        /// <param name="distance">ボリューム中心から箱までの二乗距離。</param>
        internal void AddBox(FogObstacle obstacle, Matrix4x4 worldToBox, float distance)
        {
            int index = NearestSlot(_boxDistances, ref _boxCount, distance);
            if (index >= 0)
            {
                _boxes[index] = worldToBox * _domainToWorld;
                _obstacles[index] = obstacle;
            }
        }

        private static int NearestSlot(float[] scores, ref int count, float distance)
        {
            int slot;
            if (count < scores.Length)
            {
                slot = count++;
            }
            else
            {
                slot = 0;
                for (int i = 1; i < count; ++i)
                {
                    if (scores[i] > scores[slot])
                    {
                        slot = i;
                    }
                }

                if (distance >= scores[slot])
                {
                    return -1;
                }
            }

            scores[slot] = distance;
            return slot;
        }

        private bool Allocate(Vector3 dimensions, FogQuality quality, int resolution)
        {
            if (!SupportsFormat(GraphicsFormat.R16_SFloat) || !SupportsFormat(GraphicsFormat.R16G16B16A16_SFloat))
            {
                return false;
            }

            ComputeShader asset = Resources.Load<ComputeShader>("DriftFog/DriftFogFluid");
            Texture3D noise = Resources.Load<Texture3D>("DriftFog/Noise");
            if (!asset || !noise)
            {
                return false;
            }

            _quality = quality;
            _dimensions = dimensions;
            _resolution = resolution;
            _coarseIterations = quality == FogQuality.Low ? 12 : (quality == FogQuality.High ? 28 : 20);
            _postIterations = quality == FogQuality.Low ? 5 : (quality == FogQuality.High ? 13 : 9);
            float scale = resolution / Mathf.Max(dimensions.x, Mathf.Max(dimensions.y, dimensions.z));
            _grid = new Vector3Int(GridAxis(dimensions.x * scale), GridAxis(dimensions.y * scale), GridAxis(dimensions.z * scale));
            CellSize = new Vector3(dimensions.x / _grid.x, dimensions.y / _grid.y, dimensions.z / _grid.z);
            _compute = Object.Instantiate(asset);
            _compute.hideFlags = HideFlags.HideAndDontSave;
            _raster = _compute.FindKernel("RasterizeSolids");
            _initialize = _compute.FindKernel("Initialize");
            _clearPressure = _compute.FindKernel("ClearPressure");
            _curl = _compute.FindKernel("Curl");
            _advect = _compute.FindKernel("AdvectVelocity");
            _diffuseVelocity = _compute.FindKernel("DiffuseVelocity");
            _divergence = _compute.FindKernel("Divergence");
            _jacobi = _compute.FindKernel("Jacobi");
            _restrictPressure = _compute.FindKernel("RestrictPressure");
            _solveCoarse = _compute.FindKernel("SolveCoarse");
            _prolongatePressure = _compute.FindKernel("ProlongatePressure");
            _project = _compute.FindKernel("Project");
            _predict = _compute.FindKernel("PredictDensity");
            _correct = _compute.FindKernel("CorrectDensity");
            _compute.SetInts("_GridSize", _grid.x, _grid.y, _grid.z);
            _compute.SetVector("_CellSize", CellSize);
            for (int i = 0; i < FirstDisplacement; ++i)
            {
                GraphicsFormat format = i < 3 || i == CoarseStencil ? GraphicsFormat.R16G16B16A16_SFloat : GraphicsFormat.R16_SFloat;
                int divisor = i >= CoarseStencil ? 2 : 1;
                if (!CreateField(i, divisor, format))
                {
                    return false;
                }
            }

            if (quality == FogQuality.High && _compute.HasKernel("TransportDisplacement"))
            {
                AllocateDisplacement();
            }

            _compute.SetInt("_HighAdvection", _transportDisplacement >= 0 ? 1 : 0);
            if (_transportDisplacement >= 0)
            {
                _compute.SetTexture(_transportDisplacement, "_NoiseTex", noise);
                _predictVelocity = _compute.FindKernel("PredictVelocity");
                _compute.SetTexture(_predictVelocity, "_SolidRead", Solids);
                _compute.SetTexture(_predictVelocity, "_VelocityRead", Velocity);
            }

            _densityIndex = 3;
            _compute.SetTexture(_raster, "_SolidWrite", Solids);
            int[] withSolids = { _initialize, _curl, _advect, _divergence, _jacobi, _restrictPressure, _prolongatePressure, _project, _predict, _correct };
            foreach (int kernel in withSolids)
            {
                _compute.SetTexture(kernel, "_SolidRead", Solids);
            }

            _compute.SetTexture(_initialize, "_NoiseTex", noise);
            _compute.SetTexture(_initialize, "_VelocityWrite", Velocity);
            _compute.SetTexture(_initialize, DensityWrite, Density);
            _compute.SetTexture(_curl, "_VelocityRead", Velocity);
            _compute.SetTexture(_curl, "_CurlWrite", _textures[2]);
            _compute.SetTexture(_advect, "_VelocityRead", Velocity);
            _compute.SetTexture(_advect, "_VelocityPrediction", Velocity);
            _compute.SetTexture(_advect, "_VelocityWrite", _textures[1]);
            _compute.SetTexture(_advect, "_CurlRead", _textures[2]);
            _compute.SetTexture(_divergence, "_VelocityRead", _textures[1]);
            _compute.SetTexture(_diffuseVelocity, "_SolidRead", Solids);
            _compute.SetTexture(_divergence, "_DivergenceWrite", Divergence);
            _compute.SetTexture(_clearPressure, PressureWrite, _textures[FirstPressure]);
            _compute.SetTexture(_jacobi, "_DivergenceRead", Divergence);
            _compute.SetTexture(_restrictPressure, PressureRead, _textures[FirstPressure]);
            _compute.SetTexture(_restrictPressure, "_DivergenceRead", Divergence);
            _compute.SetTexture(_restrictPressure, "_CoarseStencilWrite", _textures[CoarseStencil]);
            _compute.SetTexture(_restrictPressure, CoarsePressureWrite, _textures[FirstCoarsePressure]);
            _compute.SetTexture(_solveCoarse, "_CoarseStencilRead", _textures[CoarseStencil]);
            _compute.SetTexture(_prolongatePressure, PressureRead, _textures[FirstPressure]);
            _compute.SetTexture(_prolongatePressure, CoarsePressureRead, _textures[FirstCoarsePressure]);
            _compute.SetTexture(_prolongatePressure, PressureWrite, _textures[SecondPressure]);
            _compute.SetTexture(_project, "_VelocityRead", _textures[1]);
            _projectionInput = 1;
            _compute.SetTexture(_project, "_VelocityWrite", Velocity);
            _compute.SetTexture(_project, PressureRead, _textures[FirstPressure]);
            _compute.SetTexture(_predict, "_VelocityRead", Velocity);
            _compute.SetTexture(_predict, DensityWrite, _textures[5]);
            _compute.SetTexture(_correct, "_NoiseTex", noise);
            _compute.SetTexture(_correct, "_VelocityRead", Velocity);
            _compute.SetTexture(_correct, "_PredictedRead", _textures[5]);
            return true;
        }

        private bool CreateField(int index, int divisor, GraphicsFormat format)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(_grid.x / divisor, _grid.y / divisor, format, 0)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = _grid.z / divisor,
                msaaSamples = 1,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
            };
            RenderTexture field = new RenderTexture(descriptor)
            {
                name = "Drift Fog field " + index,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _textures[index] = field;
            return field.Create();
        }

        private void AllocateDisplacement()
        {
            // 追加格子だけ確保できない環境でも、確保済みの High 密度・速度格子で描画を続ける。
            try
            {
                if (CreateField(FirstDisplacement, 1, GraphicsFormat.R16G16B16A16_SFloat)
                    && CreateField(SecondDisplacement, 1, GraphicsFormat.R16G16B16A16_SFloat))
                {
                    int kernel = _compute.FindKernel("TransportDisplacement");
                    _compute.SetTexture(kernel, "_SolidRead", Solids);
                    _compute.SetTexture(kernel, "_VelocityRead", Velocity);
                    _transportDisplacement = kernel;
                    return;
                }
            }
            catch (Exception exception) when (exception is UnityException || exception is OutOfMemoryException || exception is ArgumentException)
            {
                Debug.LogWarning("Drift Fog の細部変位を確保できませんでした。 " + exception.Message);
            }

            ReleaseField(FirstDisplacement);
            ReleaseField(SecondDisplacement);
        }

        private static bool SupportsFormat(GraphicsFormat format)
        {
            return SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.LoadStore)
                && SystemInfo.IsFormatSupported(format, GraphicsFormatUsage.Linear);
        }

        private static int GridAxis(float requested)
        {
            return Mathf.Max(8, Mathf.RoundToInt(requested / 4f) * 4);
        }

        private bool TexturesValid()
        {
            int count = _transportDisplacement >= 0 ? _textures.Length : FirstDisplacement;
            for (int i = 0; i < count; ++i)
            {
                if (!_textures[i] || !_textures[i].IsCreated())
                {
                    return false;
                }
            }

            return true;
        }

        private void SetParameters(FogProfile profile, float dt)
        {
            _compute.SetFloat("_Dt", dt);
            _compute.SetFloat("_SimTime", _clock);
            _domainWind = _worldToDomain.MultiplyVector(profile.Wind);
            _compute.SetVector("_Wind", _domainWind);
            // 流入する霧の塊の長軸を風向に合わせる。静穏時は既定の軸を使い、領域内の密度はそのまま運ぶ。
            float horizontalSpeed = new Vector2(_domainWind.x, _domainWind.z).magnitude;
            float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.025f, 0.25f, horizontalSpeed));
            float angle = Mathf.LerpAngle(22.97f, Mathf.Atan2(_domainWind.z, _domainWind.x) * Mathf.Rad2Deg, blend) * Mathf.Deg2Rad;
            _compute.SetVector("_InflowHeading", new Vector4(Mathf.Cos(angle), Mathf.Sin(angle), 0f, 0f));
            PrepareWakes(_domainWind, profile.WakeShedding, dt);
            _compute.SetFloat("_HeightFalloff", profile.HeightFalloff);
            _compute.SetFloat("_Turbulence", profile.Turbulence);
            _compute.SetFloat("_NoiseFrequency", profile.NoiseFrequency);
            _compute.SetFloat("_Vorticity", profile.Vorticity);
            SetDiffusion(profile.KinematicViscosity, dt);
            _compute.SetFloat("_Recovery", profile.Recovery);
            _compute.SetInt("_ActorCount", _actorCount);
            if (_actorCount > 0)
            {
                _compute.SetVectorArray("_ActorPosRadius", _actorPositions);
                _compute.SetVectorArray("_ActorVelocityStrength", _actorVelocities);
            }

            Array.Copy(_actors, _previousActors, _actorCount);
            _previousActorCount = _actorCount;
            for (int i = 0; i < _actorCount; i++)
            {
                _previousPositions[i] = _actorPositions[i];
                _previousActorPhases[i] = _actorPhases[i];
            }
        }

        private void UpdateSolids()
        {
            bool changed = _boxCount != _previousBoxCount;
            for (int i = 0; !changed && i < _boxCount; ++i)
            {
                changed = _boxes[i] != _previousBoxes[i];
            }

            _compute.SetInt("_BoxCount", _boxCount);
            if (changed)
            {
                Array.Copy(_boxes, _previousBoxes, _boxCount);
                _previousBoxCount = _boxCount;
                if (_boxCount > 0)
                {
                    _compute.SetMatrixArray("_BoxFromDomain", _boxes);
                }

                Dispatch(_raster);
                // 箱の移動や再確保で境界条件が変わった場合だけ、前ステップの圧力を破棄する。
                Dispatch(_clearPressure);
            }
        }

        private void SetDiffusion(float viscosity, float dt)
        {
            _diffusionSteps = 0;
            if (viscosity <= 0f)
            {
                return;
            }

            Vector3 inverseSquare = new Vector3(1f / (CellSize.x * CellSize.x), 1f / (CellSize.y * CellSize.y), 1f / (CellSize.z * CellSize.z));
            float sum = inverseSquare.x + inverseSquare.y + inverseSquare.z;
            // 各軸の ν Δt / h² の和を 0.45 以下にし、中心を含む七点の重みを非負に保つ。
            // 極小の体積では八分割を上限とし、この場合にだけ解像可能な速度拡散量に制限する。
            float requestedSteps = viscosity * dt * sum / 0.45f;
            _diffusionSteps = Mathf.CeilToInt(Mathf.Clamp(requestedSteps, 1f, 8f));
            float step = Mathf.Min(viscosity * dt / _diffusionSteps, 0.45f / sum);
            _compute.SetVector("_DiffusionWeights", inverseSquare * step);
        }

        private void DiffuseMomentum()
        {
            int read = 1;
            for (int i = 0; i < _diffusionSteps; ++i)
            {
                int write = read == 1 ? 2 : 1;
                // 外力計算が済んだ渦度格子を再利用する。圧力射影までにすべての拡散を終える。
                _compute.SetTexture(_diffuseVelocity, "_VelocityRead", _textures[read]);
                _compute.SetTexture(_diffuseVelocity, "_VelocityWrite", _textures[write]);
                Dispatch(_diffuseVelocity);
                read = write;
            }

            if (_projectionInput != read)
            {
                _compute.SetTexture(_divergence, "_VelocityRead", _textures[read]);
                _compute.SetTexture(_project, "_VelocityRead", _textures[read]);
                _projectionInput = read;
            }
        }

        // 一様流が角柱の左右の剥離層に与える回転を、格子で解像できない境界層の代わりに供給する。
        // 下流での運動は移流・圧力射影・渦度保持に任せる。発生器の位相は障害物に結び付ける。
        private struct WakeSource
        {
            public FogObstacle Obstacle;
            public Vector3 WorldCenter;
            public float Phase;
            public Vector3 Origin;
            public Vector3 SideOffset;
            public Vector3 Axis;
            public Vector3 Bounds;
            public float HalfSpan;
            public float Radius;
            public float Frequency;
            public float Acceleration;
        }

        private void PrepareWakes(Vector3 wind, float strength, float dt)
        {
            var previous = _wakeSources;
            _wakeSources = _oldWakeSources;
            _oldWakeSources = previous;
            for (int i = 0; i < _boxCount; ++i)
            {
                Matrix4x4 geometry = _boxes[i].inverse;
                Vector3 center = geometry.MultiplyPoint3x4(Vector3.zero);
                Vector3 worldCenter = _domainToWorld.MultiplyPoint3x4(center);
                var wake = new WakeSource { Obstacle = _obstacles[i], WorldCenter = worldCenter };
                Vector3 flow = wind;
                for (int j = 0; j < _trackedBoxCount; ++j)
                {
                    if (previous[j].Obstacle != wake.Obstacle)
                    {
                        continue;
                    }

                    wake.Phase = previous[j].Phase;
                    if (!_skipBoxMotion && _sinceSample > 0.0001f)
                    {
                        Vector3 movement = _worldToDomain.MultiplyVector(worldCenter - previous[j].WorldCenter);
                        // 箱のいずれかの軸で一度に四個分を越えた移動はテレポートとして扱う。
                        Vector3 displacement = _boxes[i].MultiplyVector(movement);
                        if (Mathf.Max(Mathf.Abs(displacement.x), Mathf.Max(Mathf.Abs(displacement.y), Mathf.Abs(displacement.z))) < 4f)
                        {
                            flow -= Vector3.ClampMagnitude(movement / _sinceSample, 24f);
                        }
                    }

                    break;
                }

                if (strength > 0f && flow.sqrMagnitude > 0.000001f)
                {
                    SetWakeGeometry(ref wake, geometry, center, flow, strength, dt);
                }

                _wakeSources[i] = wake;
            }

            _trackedBoxCount = _boxCount;
            _skipBoxMotion = false;
        }

        private void SetWakeGeometry(ref WakeSource wake, Matrix4x4 geometry, Vector3 center, Vector3 flow, float strength, float dt)
        {
            float speed = flow.magnitude;
            Vector3 heading = flow / speed;
            Vector3 x = geometry.MultiplyVector(Vector3.right);
            Vector3 y = geometry.MultiplyVector(Vector3.up);
            Vector3 z = geometry.MultiplyVector(Vector3.forward);
            // 気流に対し最も長く見える辺を渦軸とする。同じ長さなら柱のローカル上方向を優先する。
            Vector3 span = Vector3.ProjectOnPlane(y, heading);
            Vector3 alternate = Vector3.ProjectOnPlane(x, heading);
            if (alternate.sqrMagnitude > span.sqrMagnitude * 1.001f)
            {
                span = alternate;
            }

            alternate = Vector3.ProjectOnPlane(z, heading);
            if (alternate.sqrMagnitude > span.sqrMagnitude * 1.001f)
            {
                span = alternate;
            }

            Vector3 axis = span.normalized;
            Vector3 side = Vector3.Cross(axis, heading);
            float width = ProjectedWidth(side, x, y, z);
            if (width <= 0.0001f)
            {
                return;
            }

            float cells = width / Mathf.Max(Vector3.Scale(side, CellSize).magnitude, Vector3.Scale(heading, CellSize).magnitude);
            float frequency = BoxStrouhal * speed / width;
            // 横断幅がほぼ一セル、または周期が四刻み未満なら格子・時刻から決まらない渦を加えない。
            float resolved = Mathf.InverseLerp(1.25f, 2.5f, cells) * (1f - Mathf.InverseLerp(0.125f, 0.25f, frequency * dt));
            if (resolved <= 0f)
            {
                return;
            }

            wake.Axis = axis;
            wake.Frequency = frequency;
            wake.Radius = 0.68f * width;
            wake.HalfSpan = 0.5f * ProjectedWidth(axis, x, y, z) + 0.15f * width;
            wake.Origin = center + heading * (0.5f * ProjectedWidth(heading, x, y, z) + 0.22f * width);
            wake.SideOffset = side * (0.42f * width);
            // 支持円柱を包む箱。GPU でワークグループ全体が届かない発生器を除外する。
            wake.Bounds = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z)) * wake.HalfSpan + Vector3.one * wake.Radius;
            wake.Acceleration = strength * resolved * Mathf.Min(7f * speed * speed / width, 1.5f * speed / dt);
        }

        private static float ProjectedWidth(Vector3 direction, Vector3 x, Vector3 y, Vector3 z)
        {
            return Mathf.Abs(Vector3.Dot(direction, x)) + Mathf.Abs(Vector3.Dot(direction, y)) + Mathf.Abs(Vector3.Dot(direction, z));
        }

        private void UploadWakes(float dt)
        {
            int count = 0;
            for (int i = 0; i < _trackedBoxCount; ++i)
            {
                ref WakeSource wake = ref _wakeSources[i];
                if (wake.Frequency <= 0f)
                {
                    continue;
                }

                float increment = wake.Frequency * dt;
                float wave = Mathf.Sin((wake.Phase + 0.5f * increment) * (2f * Mathf.PI));
                wake.Phase = Mathf.Repeat(wake.Phase + increment, 1f);
                Vector3 center = wake.Origin + Mathf.Sign(wave) * wake.SideOffset;
                _wakeCenters[count] = new Vector4(center.x, center.y, center.z, 1f / wake.Radius);
                _wakeAxes[count] = new Vector4(wake.Axis.x, wake.Axis.y, wake.Axis.z, wake.HalfSpan / wake.Radius);
                _wakeBounds[count] = new Vector4(wake.Bounds.x, wake.Bounds.y, wake.Bounds.z, -wave * Mathf.Abs(wave) * wake.Acceleration);
                ++count;
            }

            _compute.SetInt(WakeCount, count);
            if (count > 0)
            {
                _compute.SetVectorArray(WakeCenters, _wakeCenters);
                _compute.SetVectorArray(WakeAxes, _wakeAxes);
                _compute.SetVectorArray(WakeBounds, _wakeBounds);
            }
        }

        private void UploadActorShedding(float strength, float dt)
        {
            if (_actorCount == 0)
            {
                return;
            }

            for (int i = 0; i < _actorCount; ++i)
            {
                if (strength <= 0f)
                {
                    _actorShedding[i] = Vector4.zero;
                    continue;
                }

                Vector3 relative = (Vector3)_actorVelocities[i] - _domainWind;
                float diameter = 2f * _actorPositions[i].w;
                float increment = ActorStrouhal * relative.magnitude * dt / diameter;
                float middle = Mathf.Repeat(_actorPhases[i] + 0.5f * increment, 1f);
                _actorPhases[i] = Mathf.Repeat(_actorPhases[i] + increment, 1f);
                _previousActorPhases[i] = _actorPhases[i];

                // 一周期で左右に一個ずつ離す。発生中だけ芯が肩から内側・下流へずれ、
                // 半周期の境目では振幅が滑らかにゼロに戻る。離れた渦を固定位置から加速し続けない。
                float life = Mathf.Repeat(2f * middle, 1f);
                float age = Mathf.SmoothStep(0f, 1f, life);
                float side = middle < 0.5f ? 1f : -1f;
                float pulse = Mathf.Sin(Mathf.PI * life);
                float horizontalCell = Mathf.Max(CellSize.x, CellSize.z);
                float resolved = Mathf.InverseLerp(2f, 4.5f, diameter / horizontalCell)
                                 * (1f - Mathf.InverseLerp(0.125f, 0.25f, increment));
                float gain = strength * resolved;
                _actorShedding[i] = new Vector4(-0.67f - 0.56f * age, side * (0.68f - 0.28f * age),
                    side * pulse * pulse * gain, Mathf.Clamp01(gain));
            }

            _compute.SetVectorArray(ActorShedding, _actorShedding);
        }

        private void Step(float vorticity, float wakeShedding, float dt)
        {
            _compute.SetFloat("_SimTime", _clock);
            if (vorticity > 0f)
            {
                Dispatch(_curl);
            }

            UploadWakes(dt);
            UploadActorShedding(wakeShedding, dt);
            if (_transportDisplacement >= 0)
            {
                // 次の細部変位の書込先は射影まで空いている。純粋な速度・層の一次予測を一時保存する。
                var prediction = _textures[_displacementIndex == FirstDisplacement ? SecondDisplacement : FirstDisplacement];
                _compute.SetTexture(_predictVelocity, "_VelocityWrite", prediction);
                Dispatch(_predictVelocity);
                _compute.SetTexture(_advect, "_VelocityPrediction", prediction);
            }

            _compute.SetTexture(_advect, DensityRead, Density);
            Dispatch(_advect);
            DiffuseMomentum();
            Dispatch(_divergence);
            // 細かい格子で局所誤差をならし、粗い格子で低周波残差を補正する。
            SmoothPressure(FirstPressure, 2);
            DispatchCoarse(_restrictPressure);
            for (int i = 0; i < _coarseIterations; ++i)
            {
                bool even = (i & 1) == 0;
                _compute.SetTexture(_solveCoarse, CoarsePressureRead, _textures[even ? FirstCoarsePressure : SecondCoarsePressure]);
                _compute.SetTexture(_solveCoarse, CoarsePressureWrite, _textures[even ? SecondCoarsePressure : FirstCoarsePressure]);
                DispatchCoarse(_solveCoarse);
            }

            Dispatch(_prolongatePressure);
            SmoothPressure(SecondPressure, _postIterations);
            Dispatch(_project);
            if (_transportDisplacement >= 0)
            {
                _compute.SetInt(DisplacementHasHistory, _hasDisplacement ? 1 : 0);
                _compute.SetTexture(_transportDisplacement, DisplacementRead, _textures[_displacementIndex]);
                _displacementIndex = _displacementIndex == FirstDisplacement ? SecondDisplacement : FirstDisplacement;
                _compute.SetTexture(_transportDisplacement, DisplacementWrite, _textures[_displacementIndex]);
                Dispatch(_transportDisplacement);
                _hasDisplacement = true;
                _driftX += (double)_domainWind.x * dt;
                _driftY += (double)_domainWind.y * dt;
                _driftZ += (double)_domainWind.z * dt;
            }

            _compute.SetTexture(_predict, DensityRead, Density);
            Dispatch(_predict);
            _compute.SetTexture(_correct, DensityRead, Density);
            _densityIndex = _densityIndex == 3 ? 4 : 3;
            _compute.SetTexture(_correct, DensityWrite, Density);
            Dispatch(_correct);
        }

        private void SmoothPressure(int read, int iterations)
        {
            for (int i = 0; i < iterations; ++i)
            {
                int write = read == FirstPressure ? SecondPressure : FirstPressure;
                _compute.SetTexture(_jacobi, PressureRead, _textures[read]);
                _compute.SetTexture(_jacobi, PressureWrite, _textures[write]);
                Dispatch(_jacobi);
                read = write;
            }
        }

        private void DispatchCoarse(int kernel)
        {
            _compute.Dispatch(kernel, (_grid.x + 7) / 8, (_grid.y + 7) / 8, (_grid.z + 7) / 8);
        }

        private void Dispatch(int kernel)
        {
            _compute.Dispatch(kernel, _grid.x / 4, _grid.y / 4, _grid.z / 4);
        }

        /// <summary>再確保や無効化に備え、格子と専用 ComputeShader を解放します。</summary>
        public void Dispose()
        {
            for (int i = 0; i < _textures.Length; ++i)
            {
                ReleaseField(i);
            }

            if (_compute)
            {
                DestroyObject(_compute);
                _compute = null;
            }

            _clock = 0f;
            _accumulator = 0f;
            _sinceSample = 0f;
            _previousBoxCount = -1;
            _previousActorCount = 0;
            _trackedBoxCount = 0;
            _skipBoxMotion = true;
            _densityIndex = 3;
            _transportDisplacement = -1;
            _displacementIndex = FirstDisplacement;
            _hasDisplacement = false;
            _driftX = 0d;
            _driftY = 0d;
            _driftZ = 0d;
            CellSize = Vector3.zero;
        }

        private void ReleaseField(int index)
        {
            if (_textures[index])
            {
                _textures[index].Release();
                DestroyObject(_textures[index]);
                _textures[index] = null;
            }
        }

        private static void DestroyObject(Object asset)
        {
            if (Application.isPlaying)
            {
                Object.Destroy(asset);
            }
            else
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
