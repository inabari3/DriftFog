using System.Collections.Generic;
using UnityEngine;

namespace DriftFog
{
    /// <summary>球状の影響範囲内から霧を押し出し、通過位置に減衰する航跡を残す。</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Drift Fog/Fog Interactor")]
    [DefaultExecutionOrder(100)]
    public sealed class FogInteractor : MonoBehaviour
    {
        private const int TrailCapacity = 8;
        private static readonly List<FogInteractor> Active = new List<FogInteractor>();
        [SerializeField, Min(0.05f)] private float _radius = 1.5f;
        [SerializeField, Range(0f, 1f)] private float _strength = 1f;
        [SerializeField, Range(0f, 10f)] private float _trailLifetime = 2.4f;
        private readonly Vector3[] _points = new Vector3[TrailCapacity];
        private readonly float[] _birth = new float[TrailCapacity];
        private Vector3 _anchor;
        private int _next;
        private int _count;

        /// <summary>Transform の拡縮から独立した作用半径（メートル）。</summary>
        public float Radius
        {
            get => Mathf.Max(0.05f, _radius); set => _radius = value;
        }

        /// <summary>中心で霧を押し出す割合。</summary>
        public float Strength
        {
            get => Mathf.Clamp01(_strength); set => _strength = value;
        }

        /// <summary>航跡が埋まるまでの秒数。ゼロにすると現在位置だけが影響する。</summary>
        public float TrailLifetime
        {
            get => Mathf.Clamp(_trailLifetime, 0f, 10f); set => _trailLifetime = value;
        }

        /// <summary>記録した航跡を消し、現在の位置を次の起点にする。</summary>
        public void ClearTrail()
        {
            _anchor = transform.position;
            _next = 0;
            _count = 0;
        }

        /// <summary>体積に重なる作用点を現在位置、次いで航跡の順に候補へ加える。</summary>
        /// <param name="volume">作用点を選択する体積。</param>
        /// <param name="now">移動記録と同じ時計による時刻。</param>
        internal static void Collect(DriftFogVolume volume, float now)
        {
            for (int i = 0; i < Active.Count; ++i)
            {
                FogInteractor item = Active[i];
                if (item == null || item.Strength <= 0f || !volume.SharesEnvironment(item.gameObject))
                {
                    continue;
                }

                volume.ConsiderSource(item.transform.position, item.Radius, item.Strength, true);
                float lifetime = item.TrailLifetime;
                if (lifetime <= 0f)
                {
                    continue;
                }

                for (int j = 0; j < item._count; ++j)
                {
                    int index = (item._next + TrailCapacity - j - 1) % TrailCapacity;
                    float age = Mathf.Max(0f, now - item._birth[index]) / lifetime;
                    if (age >= 1f)
                    {
                        break;
                    }

                    float fade = 1f - Mathf.SmoothStep(0f, 1f, age);
                    volume.ConsiderSource(item._points[index], item.Radius * (1f + 0.15f * age), item.Strength * fade * 0.92f, false);
                }
            }
        }

        /// <summary>一定の距離を移動したときに元の位置を記録する。</summary>
        /// <param name="now">航跡の生成時刻。</param>
        internal void Capture(float now)
        {
            Vector3 current = transform.position;
            float spacing = Mathf.Max(0.2f, Radius * 0.52f);
            float distance = Vector3.Distance(_anchor, current);
            if (distance < spacing)
            {
                return;
            }

            if (TrailLifetime <= 0f || distance > spacing * TrailCapacity)
            {
                ClearTrail();
                return;
            }

            Vector3 direction = (current - _anchor) / distance;
            int samples = Mathf.FloorToInt(distance / spacing);
            for (int i = 0; i < samples; ++i)
            {
                _points[_next] = _anchor;
                _birth[_next] = now;
                _next = (_next + 1) % TrailCapacity;
                _count = Mathf.Min(TrailCapacity, _count + 1);
                _anchor += direction * spacing;
            }
        }

        private void OnEnable()
        {
            ClearTrail();
            Active.Add(this);
        }

        /// <summary>格子に届く現在の移動体を登録し、速度と排除範囲を与えます。</summary>
        /// <param name="volume">対象ボリューム。</param>
        /// <param name="simulation">今回更新する格子。</param>
        /// <param name="bounds">ワールド空間のボリューム境界。</param>
        internal static void CollectFluid(DriftFogVolume volume, FogFluidSimulation simulation, Bounds bounds)
        {
            for (int i = 0; i < Active.Count; i++)
            {
                FogInteractor actor = Active[i];
                if (!actor || actor.Strength <= 0f || !volume.SharesEnvironment(actor.gameObject))
                {
                    continue;
                }

                Vector3 position = actor.transform.position;
                float radius = actor.Radius;
                if (bounds.SqrDistance(position) <= 12.25f * radius * radius)
                {
                    simulation.AddActor(actor, position, radius, actor.Strength);
                }
            }
        }

        private void LateUpdate()
        {
            if (Application.IsPlaying(gameObject))
            {
                Capture(Time.time);
            }
        }

        private void OnDisable()
        {
            Active.Remove(this);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.94f, 0.82f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, Radius);
        }
    }
}
