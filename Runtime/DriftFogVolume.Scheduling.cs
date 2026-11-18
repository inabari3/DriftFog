using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriftFog
{
    /// <content>全カメラの可視範囲と距離から、自動更新する流体格子を選ぶ。</content>
    public sealed partial class DriftFogVolume
    {
        private const int DefaultFluidBudget = 4;
        private static readonly List<DriftFogVolume> Volumes = new List<DriftFogVolume>();
        private static readonly List<DriftFogVolume> Candidates = new List<DriftFogVolume>();
        private static readonly IComparer<DriftFogVolume> PriorityOrder = Comparer<DriftFogVolume>.Create(ComparePriority);
        private static readonly Plane[] CameraPlanes = new Plane[6];
        private static int _maxSimulatedVolumes = DefaultFluidBudget;
        private int _observationFrame = -1;
        private float _nearestDistance = float.PositiveInfinity;
        private float _previousDistance = float.PositiveInfinity;
        private float _rank;
        private float _retryBudgetTime;
        private bool _visibleInContext;
        private bool _requestedInContext;
        private bool _selected;

        /// <summary>全カメラで共有する、自動的に維持・更新する流体格子の上限。初期値は4。ゼロで自動描画を解析表現に切り替えます。</summary>
        public static int MaxSimulatedVolumes
        {
            get => _maxSimulatedVolumes;
            set => _maxSimulatedVolumes = Mathf.Max(0, value);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetBudget()
        {
            _maxSimulatedVolumes = DefaultFluidBudget;
        }

        private void RegisterVolume()
        {
            if (Volumes.Count == 0)
            {
                RenderPipelineManager.beginContextRendering += PrepareForCameras;
            }

            _observationFrame = -1;
            _nearestDistance = float.PositiveInfinity;
            _previousDistance = float.PositiveInfinity;
            _retryBudgetTime = 0f;
            Volumes.Add(this);
        }

        private void UnregisterVolume()
        {
            Volumes.Remove(this);
            Candidates.Remove(this);
            if (Volumes.Count == 0)
            {
                RenderPipelineManager.beginContextRendering -= PrepareForCameras;
            }
        }

        private static void PrepareForCameras(ScriptableRenderContext context, List<Camera> cameras)
        {
            int frame = Time.frameCount;
            foreach (DriftFogVolume volume in Volumes)
            {
                if (volume._observationFrame != frame)
                {
                    // 別の描画コンテキストにいる分割画面・シーンビューの直前の観測を一フレーム保持する。
                    volume._previousDistance = volume._observationFrame == frame - 1 ? volume._nearestDistance : float.PositiveInfinity;
                    volume._observationFrame = frame;
                    volume._nearestDistance = float.PositiveInfinity;
                }

                volume._visibleInContext = false;
                volume._requestedInContext = false;
                if (volume._renderer)
                {
                    volume._bounds = volume._renderer.bounds;
                }
            }

            bool hasSimulationCamera = false;
            foreach (Camera camera in cameras)
            {
                if (!camera || camera.cameraType == CameraType.Preview)
                {
                    continue;
                }

                bool simulate = camera.cameraType != CameraType.Reflection;
                hasSimulationCamera |= simulate;
                GeometryUtility.CalculateFrustumPlanes(camera.cullingMatrix, CameraPlanes);
                Vector3 eye = camera.transform.position;
                foreach (DriftFogVolume volume in Volumes)
                {
                    FogProfile profile = volume.Profile;
                    if (!profile || !volume._renderer || !volume._renderer.enabled || (camera.cullingMask & (1 << volume.gameObject.layer)) == 0)
                    {
                        continue;
                    }

                    float distance = volume._bounds.SqrDistance(eye);
                    if (distance > profile.MaxDistance * profile.MaxDistance || !GeometryUtility.TestPlanesAABB(CameraPlanes, volume._bounds))
                    {
                        continue;
                    }

                    volume._visibleInContext = true;
                    if (simulate)
                    {
                        volume._requestedInContext = true;
                        volume._nearestDistance = Mathf.Min(volume._nearestDistance, distance);
                    }
                }
            }

            if (hasSimulationCamera)
            {
                SelectVolumes(frame);
            }

            foreach (DriftFogVolume volume in Volumes)
            {
                if (!volume._visibleInContext)
                {
                    continue;
                }

                bool stepped = volume._selected && volume._requestedInContext && volume._lastSimulationFrame != frame;
                if (stepped)
                {
                    if (volume._fluid != null && volume._lastSimulationFrame != frame - 1)
                    {
                        volume._fluid.ResetActorHistory();
                    }

                    float dt = Application.IsPlaying(volume.gameObject) ? Time.deltaTime : Mathf.Min(0.1f, Time.realtimeSinceStartup - volume._lastVisibleTime);
                    volume.StepSimulation(volume._fluidActive ? dt : 0f);
                }

                if (volume._lastRefreshFrame != frame)
                {
                    volume.Refresh();
                }
                else if (stepped)
                {
                    volume._renderer.SetPropertyBlock(volume._block);
                }
            }
        }

        private static void SelectVolumes(int frame)
        {
            Candidates.Clear();
            int available = MaxSimulatedVolumes;
            float now = Time.realtimeSinceStartup;
            foreach (DriftFogVolume volume in Volumes)
            {
                volume._selected = false;
                if (volume._manualFrame == frame && volume._fluidActive)
                {
                    --available;
                    continue;
                }

                FogProfile profile = volume.Profile;
                float distance = Mathf.Min(volume._previousDistance, volume._nearestDistance);
                if (!profile || !profile.SimulateFluid || !volume._renderer || !volume._renderer.enabled || now < volume._retryBudgetTime
                    || (float.IsPositiveInfinity(distance) && !volume._fluidActive))
                {
                    continue;
                }

                // 既に処理した格子はこのフレーム中固定する。既存の格子は距離に一割の余裕を持たせ、境目での再確保を抑える。
                volume._rank = volume._lastSimulationFrame == frame ? float.NegativeInfinity : distance * (volume._fluidActive ? 0.81f : 1f);
                Candidates.Add(volume);
            }

            if (available > 0 && Candidates.Count > available)
            {
                Candidates.Sort(PriorityOrder);
            }

            for (int i = 0; i < Candidates.Count && i < available; ++i)
            {
                Candidates[i]._selected = true;
            }

            // 先に退去させ、高品質格子の入れ替え中も自動確保数を予算内に保つ。
            foreach (DriftFogVolume volume in Volumes)
            {
                if (!volume._selected && volume._manualFrame != frame && volume._fluid != null)
                {
                    volume.ReleaseFluid();
                }
            }
        }

        private static int ComparePriority(DriftFogVolume first, DriftFogVolume second)
        {
            int order = first._rank.CompareTo(second._rank);
            return order != 0 ? order : first.GetInstanceID().CompareTo(second.GetInstanceID());
        }
    }
}
