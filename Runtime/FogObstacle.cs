using System.Collections.Generic;
using UnityEngine;

namespace DriftFog
{
    /// <summary>Box Collider の輪郭から霧を押し出し、外縁に薄い吹き溜まりを作る。</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BoxCollider))]
    [AddComponentMenu("Drift Fog/Fog Obstacle")]
    public sealed class FogObstacle : MonoBehaviour
    {
        private const int Limit = 2;
        private static readonly List<FogObstacle> Active = new List<FogObstacle>();
        private static readonly Vector4[] Centers = new Vector4[Limit];
        private static readonly Vector4[] AxesX = new Vector4[Limit];
        private static readonly Vector4[] AxesY = new Vector4[Limit];
        private static readonly Vector4[] AxesZ = new Vector4[Limit];
        private static readonly float[] Distances = new float[Limit];
        private static readonly int CountId = Shader.PropertyToID("_FogObstacleCount");
        private static readonly int CentersId = Shader.PropertyToID("_FogObstacleCenters");
        private static readonly int AxesXId = Shader.PropertyToID("_FogObstacleX");
        private static readonly int AxesYId = Shader.PropertyToID("_FogObstacleY");
        private static readonly int AxesZId = Shader.PropertyToID("_FogObstacleZ");

        [SerializeField, Min(0.02f)] private float _edgeWidth = 0.55f;
        private BoxCollider _box;

        /// <summary>霧が輪郭に沿って押し出される外側の幅。単位はワールド空間のメートル。</summary>
        public float EdgeWidth
        {
            get => Mathf.Max(0.02f, _edgeWidth); set => _edgeWidth = value;
        }

        /// <summary>境界が霧に届く障害物から体積中心に近い二つを選び、現在の輪郭を設定する。</summary>
        /// <param name="volume">表示環境を判定する霧ボリューム。</param>
        /// <param name="bounds">ボリュームのワールド空間の境界。</param>
        /// <param name="block">レンダラーに送るプロパティ。</param>
        internal static void Apply(DriftFogVolume volume, Bounds bounds, MaterialPropertyBlock block)
        {
            int count = 0;
            for (int i = 0; i < Active.Count; ++i)
            {
                FogObstacle obstacle = Active[i];
                if (!obstacle || !obstacle._box || !obstacle._box.enabled || !volume.SharesEnvironment(obstacle.gameObject))
                {
                    continue;
                }

                Transform pose = obstacle.transform;
                Vector3 center = pose.TransformPoint(obstacle._box.center);
                Vector3 extents = Absolute(Vector3.Scale(obstacle._box.size, pose.lossyScale)) * 0.5f;
                Vector3 right = pose.right;
                Vector3 up = pose.up;
                Vector3 forward = pose.forward;
                Vector3 halfBounds = Absolute(right) * extents.x + Absolute(up) * extents.y + Absolute(forward) * extents.z;
                float width = obstacle.EdgeWidth;
                Bounds influence = new Bounds(center, (halfBounds + Vector3.one * width) * 2f);
                if (!bounds.Intersects(influence))
                {
                    continue;
                }

                // 中心間の距離を使うと、長い壁の中央が遠い場合に手前の端を取りこぼす。
                Vector3 offset = bounds.center - center;
                Vector3 local = new Vector3(Vector3.Dot(offset, right), Vector3.Dot(offset, up), Vector3.Dot(offset, forward));
                float distance = Vector3.Max(Vector3.zero, Absolute(local) - extents).sqrMagnitude;
                int slot = count;
                if (count == Limit)
                {
                    slot = Distances[0] >= Distances[1] ? 0 : 1;
                    if (distance >= Distances[slot])
                    {
                        continue;
                    }
                }
                else
                {
                    ++count;
                }

                Distances[slot] = distance;
                Centers[slot] = new Vector4(center.x, center.y, center.z, width);
                AxesX[slot] = new Vector4(right.x, right.y, right.z, extents.x);
                AxesY[slot] = new Vector4(up.x, up.y, up.z, extents.y);
                AxesZ[slot] = new Vector4(forward.x, forward.y, forward.z, extents.z);
            }

            // SetVectorArray は各ブロックに値をコピーするため、作業領域を全ボリュームで共有できる。
            block.SetFloat(CountId, count);
            if (count > 0)
            {
                block.SetVectorArray(CentersId, Centers);
                block.SetVectorArray(AxesXId, AxesX);
                block.SetVectorArray(AxesYId, AxesY);
                block.SetVectorArray(AxesZId, AxesZ);
            }
        }

        private static Vector3 Absolute(Vector3 value)
        {
            return new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        }

        /// <summary>体積に接する箱を流体格子へ登録します。</summary>
        /// <param name="volume">対象ボリューム。</param>
        /// <param name="simulation">今回更新する格子。</param>
        /// <param name="bounds">ワールド空間のボリューム境界。</param>
        internal static void CollectFluid(DriftFogVolume volume, FogFluidSimulation simulation, Bounds bounds)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                FogObstacle obstacle = Active[i];
                if (!obstacle || !obstacle._box || !obstacle._box.enabled || !volume.SharesEnvironment(obstacle.gameObject))
                {
                    continue;
                }

                BoxCollider box = obstacle._box;
                Transform pose = box.transform;
                Vector3 size = Absolute(Vector3.Scale(box.size, pose.lossyScale));
                if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < 0.0001f)
                {
                    continue;
                }

                Vector3 center = pose.TransformPoint(box.center);
                Vector3 extent = (Absolute(pose.right) * size.x + Absolute(pose.up) * size.y + Absolute(pose.forward) * size.z) * 0.5f;
                Bounds boxBounds = new Bounds(center, 2f * extent);
                if (boxBounds.Intersects(bounds))
                {
                    simulation.AddBox(obstacle, Matrix4x4.TRS(center, pose.rotation, size).inverse, boxBounds.SqrDistance(bounds.center));
                }
            }
        }

        private void OnEnable()
        {
            _box = GetComponent<BoxCollider>();
            Active.Add(this);
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }
    }
}
