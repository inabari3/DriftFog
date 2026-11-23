using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DriftFog.Editor
{
    /// <summary>シーンへの体積の追加と、その描画に必要な設定の確認を提供する。</summary>
    [CustomEditor(typeof(DriftFogVolume))]
    [CanEditMultipleObjects]
    public sealed class FogPlacement : UnityEditor.Editor
    {
        private MaterialPropertyBlock _previewState;

        /// <summary>プロファイルと現在のパイプライン設定を表示する。</summary>
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            if (!pipeline || pipeline.GetType().Name != "UniversalRenderPipelineAsset")
            {
                EditorGUILayout.HelpBox("Drift Fog requires a URP 3D renderer.", MessageType.Warning);
            }
            else
            {
                SerializedProperty depth = new SerializedObject(pipeline).FindProperty("m_RequireDepthTexture");
                if (depth != null && !depth.boolValue)
                {
                    EditorGUILayout.HelpBox("Enable Depth Texture in the active URP Asset, or enable Requires Depth Texture on the camera.", MessageType.Warning);
                }
            }

            DriftFogVolume volume = (DriftFogVolume)target;
            FogProfile profile = volume.Profile;
            if (Application.isPlaying && volume.isActiveAndEnabled && profile)
            {
                // Editor の構築中はネイティブ API を呼べないため、再生状態を初めて描画するときに確保する。
                _previewState ??= new MaterialPropertyBlock();
                volume.GetComponent<MeshRenderer>().GetPropertyBlock(_previewState);
                RenderTexture grid = _previewState.GetTexture("_FogFluidDensity") as RenderTexture;
                if (grid && _previewState.GetFloat("_FogFluidActive") > 0.5f)
                {
                    EditorGUILayout.LabelField("Active fluid grid", $"{grid.width} × {grid.height} × {grid.volumeDepth}");
                }
                else
                {
                    EditorGUILayout.LabelField("Rendering", "Analytic fog");
                }
            }

            if (profile && GUILayout.Button("Select Active Profile"))
            {
                Selection.activeObject = profile;
            }
        }

        [MenuItem("GameObject/Effects/Drift Fog Volume", false, 12)]
        private static void CreateVolume(MenuCommand command)
        {
            GameObject instance = new GameObject("Drift Fog Volume");
            Undo.RegisterCreatedObjectUndo(instance, "Place Drift Fog Volume");
            GameObject parent = command.context as GameObject;
            if (parent)
            {
                GameObjectUtility.SetParentAndAlign(instance, parent);
            }
            else if (SceneView.lastActiveSceneView)
            {
                instance.transform.position = SceneView.lastActiveSceneView.pivot;
            }

            instance.transform.localScale = new Vector3(14f, 3f, 12f);
            Undo.AddComponent<DriftFogVolume>(instance);
            Selection.activeGameObject = instance;
        }
    }
}
