#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Splines;
#endif

using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using Interpolators = UnityEngine.Splines.Interpolators;

namespace Unity.Splines.Examples
{
    [ExecuteInEditMode]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer), typeof(MeshRenderer), typeof(MeshFilter))]
    public class CreateSplineMesh : MonoBehaviour
    {
        [Header("★ 벨트 형태 설정 ★")]
        [Tooltip("컨베이어 벨트의 전체 넓이를 조절합니다.")]
        [Range(0.1f, 5f)]
        public float globalWidth = 1f;

        [Tooltip("곡선의 부드러움 정도 (높을수록 촘촘해짐)")]
        [Range(1, 10)]
        [SerializeField] int m_SegmentsPerMeter = 1;

        [Header("★ 머티리얼 및 텍스처 ★")]
        [SerializeField] Material m_Material;
        [SerializeField] float m_TextureScale = 1f;

        [Header("고급 스플라인 데이터 (Index는 0.0 ~ 1.0 사이의 비율입니다)")]
        [SerializeField]
        List<SplineData<float>> m_Widths = new List<SplineData<float>>();

        public List<SplineData<float>> Widths
        {
            get
            {
                foreach (var width in m_Widths)
                {
                    if (width.DefaultValue == 0) width.DefaultValue = 1f;
                }
                return m_Widths;
            }
        }

        [SerializeField, HideInInspector] SplineContainer m_Spline;
        [SerializeField, HideInInspector] Mesh m_Mesh;

        public SplineContainer Container
        {
            get { if (m_Spline == null) m_Spline = GetComponent<SplineContainer>(); return m_Spline; }
            set => m_Spline = value;
        }

        public IReadOnlyList<Spline> splines => LoftSplines;
        public IReadOnlyList<Spline> LoftSplines
        {
            get { if (m_Spline == null) m_Spline = GetComponent<SplineContainer>(); return m_Spline?.Splines; }
        }

        public Mesh LoftMesh
        {
            get
            {
                if (m_Mesh != null) return m_Mesh;
                m_Mesh = new Mesh();
                if (m_Material != null) GetComponent<MeshRenderer>().sharedMaterial = m_Material;
                return m_Mesh;
            }
        }

        public int SegmentsPerMeter => Mathf.Min(10, Mathf.Max(1, m_SegmentsPerMeter));

        List<Vector3> m_Positions = new List<Vector3>();
        List<Vector3> m_Normals = new List<Vector3>();
        List<Vector2> m_Textures = new List<Vector2>();
        List<int> m_Indices = new List<int>();
        bool m_LoftRoadsRequested = false;

        private void RequestEditorUpdate()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Repaint();
            }
#endif
        }

        private void OnValidate()
        {
            m_LoftRoadsRequested = true;
            RequestEditorUpdate();
        }

        void Update()
        {
            if (m_LoftRoadsRequested)
            {
                LoftAllRoads();
                m_LoftRoadsRequested = false;
            }
        }

        public void OnEnable()
        {
            if (m_Mesh != null) m_Mesh = null;
            if (m_Spline == null) m_Spline = GetComponent<SplineContainer>();

            LoftAllRoads();

#if UNITY_EDITOR
            EditorSplineUtility.AfterSplineWasModified += OnAfterSplineWasModified;
            EditorSplineUtility.RegisterSplineDataChanged<float>(OnAfterSplineDataWasModified);
            Undo.undoRedoPerformed += LoftAllRoads;
#endif

            SplineContainer.SplineAdded += OnSplineContainerAdded;
            SplineContainer.SplineRemoved += OnSplineContainerRemoved;
            SplineContainer.SplineReordered += OnSplineContainerReordered;
            Spline.Changed += OnSplineChanged;
        }

        public void OnDisable()
        {
#if UNITY_EDITOR
            EditorSplineUtility.AfterSplineWasModified -= OnAfterSplineWasModified;
            EditorSplineUtility.UnregisterSplineDataChanged<float>(OnAfterSplineDataWasModified);
            Undo.undoRedoPerformed -= LoftAllRoads;
#endif
            if (m_Mesh != null)
            {
#if UNITY_EDITOR
                if (!AssetDatabase.Contains(m_Mesh)) DestroyImmediate(m_Mesh);
#else
                Destroy(m_Mesh);
#endif
            }

            SplineContainer.SplineAdded -= OnSplineContainerAdded;
            SplineContainer.SplineRemoved -= OnSplineContainerRemoved;
            SplineContainer.SplineReordered -= OnSplineContainerReordered;
            Spline.Changed -= OnSplineChanged;
        }

        void OnSplineContainerAdded(SplineContainer container, int index) { if (container == m_Spline) { m_LoftRoadsRequested = true; RequestEditorUpdate(); } }
        void OnSplineContainerRemoved(SplineContainer container, int index) { if (container == m_Spline) { m_LoftRoadsRequested = true; RequestEditorUpdate(); } }
        void OnSplineContainerReordered(SplineContainer container, int previousIndex, int newIndex) { if (container == m_Spline) { m_LoftRoadsRequested = true; RequestEditorUpdate(); } }

        void OnAfterSplineWasModified(Spline s) { m_LoftRoadsRequested = true; RequestEditorUpdate(); }
        void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification) { m_LoftRoadsRequested = true; RequestEditorUpdate(); }
        void OnAfterSplineDataWasModified(SplineData<float> splineData) { m_LoftRoadsRequested = true; RequestEditorUpdate(); }

        public void LoftAllRoads()
        {
            LoftMesh.Clear();
            m_Positions.Clear();
            m_Normals.Clear();
            m_Textures.Clear();
            m_Indices.Clear();

            if (LoftSplines == null) return;
            for (var i = 0; i < LoftSplines.Count; i++) Loft(LoftSplines[i], i);

            LoftMesh.SetVertices(m_Positions);
            LoftMesh.SetNormals(m_Normals);
            LoftMesh.SetUVs(0, m_Textures);
            LoftMesh.subMeshCount = 1;
            LoftMesh.SetIndices(m_Indices, MeshTopology.Triangles, 0);
            LoftMesh.UploadMeshData(false);

            GetComponent<MeshFilter>().sharedMesh = m_Mesh;
        }

        public void Loft(Spline spline, int widthDataIndex)
        {
            if (spline == null || spline.Count < 2) return;
            LoftMesh.Clear();
            float length = spline.GetLength();
            if (length <= 0.001f) return;

            var segmentsPerLength = SegmentsPerMeter * length;
            var segments = Mathf.CeilToInt(segmentsPerLength);
            var segmentStepT = (1f / SegmentsPerMeter) / length;
            var steps = segments + 1;
            var vertexCount = steps * 2;
            var triangleCount = segments * 6;
            var prevVertexCount = m_Positions.Count;

            var t = 0f;
            for (int i = 0; i < steps; i++)
            {
                SplineUtility.Evaluate(spline, t, out var pos, out var dir, out var up);

                if (math.length(dir) == 0)
                {
                    var nextPos = spline.GetPointAtLinearDistance(t, 0.01f, out _);
                    dir = math.normalizesafe(nextPos - pos);
                    if (math.length(dir) == 0) dir = new float3(0, 0, 1);
                }

                var scale = transform.lossyScale;
                var tangent = math.normalizesafe(math.cross(up, dir)) * new float3(1f / scale.x, 1f / scale.y, 1f / scale.z);

                var w = globalWidth;
                if (widthDataIndex < m_Widths.Count)
                {
                    float localW = m_Widths[widthDataIndex].DefaultValue;
                    if (m_Widths[widthDataIndex] != null && m_Widths[widthDataIndex].Count > 0)
                    {
                        // PathIndexUnit.Normalized = t(0~1) 기준으로 보간함!
                        localW = m_Widths[widthDataIndex].Evaluate(spline, t, PathIndexUnit.Normalized, new Interpolators.LerpFloat());
                        localW = math.clamp(localW, .001f, 10000f);
                    }
                    w *= localW;
                }

                m_Positions.Add(pos - (tangent * w));
                m_Positions.Add(pos + (tangent * w));
                m_Normals.Add(up);
                m_Normals.Add(up);
                m_Textures.Add(new Vector2(0f, t * m_TextureScale));
                m_Textures.Add(new Vector2(1f, t * m_TextureScale));

                t = math.min(1f, t + segmentStepT);
            }

            for (int i = 0, n = prevVertexCount; i < triangleCount; i += 6, n += 2)
            {
                m_Indices.Add((n + 2) % (prevVertexCount + vertexCount));
                m_Indices.Add((n + 1) % (prevVertexCount + vertexCount));
                m_Indices.Add((n + 0) % (prevVertexCount + vertexCount));
                m_Indices.Add((n + 2) % (prevVertexCount + vertexCount));
                m_Indices.Add((n + 3) % (prevVertexCount + vertexCount));
                m_Indices.Add((n + 1) % (prevVertexCount + vertexCount));
            }
        }

#if UNITY_EDITOR
        [ContextMenu("★★ 원클릭 최적화 프리팹 만들기 (Bake & Create Prefab)")]
        public void CreateOptimizedPrefab()
        {
            if (m_Mesh == null || m_Mesh.vertexCount == 0) return;

            string defaultName = gameObject.name + "_Conveyor";
            string path = EditorUtility.SaveFilePanelInProject("컨베이어 프리팹 저장", defaultName, "prefab", "저장할 위치를 선택하세요.");
            if (string.IsNullOrEmpty(path)) return;

            string prefabPath = path;
            string meshPath = path.Replace(".prefab", ".asset");

            Mesh bakedMesh = Instantiate(m_Mesh);
            bakedMesh.name = gameObject.name + "_Mesh";
            AssetDatabase.CreateAsset(bakedMesh, meshPath);

            GameObject clone = Instantiate(gameObject);
            clone.name = gameObject.name + "_Optimized";
            clone.GetComponent<MeshFilter>().sharedMesh = bakedMesh;

            DestroyImmediate(clone.GetComponent<CreateSplineMesh>());
            DestroyImmediate(clone.GetComponent<SplineContainer>());

            PrefabUtility.SaveAsPrefabAsset(clone, prefabPath);

            DestroyImmediate(clone);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"<color=cyan>[프리팹 자동화 성공!]</color> {prefabPath}");
        }

        // =======================================================
        // [시각화] 사용자가 입력한 Data Point의 위치를 직접 보여줌!
        // =======================================================
        private void OnDrawGizmosSelected()
        {
            if (m_Spline == null) return;
            var splines = m_Spline.Splines;
            if (splines == null) return;

            for (int i = 0; i < splines.Count; i++)
            {
                var spline = splines[i];

                // 1. 기본 뼈대(Knot)는 눈에 덜 띄는 작은 흰색 점으로 표시
                Gizmos.color = Color.white;
                for (int k = 0; k < spline.Count; k++)
                {
                    Vector3 knotPos = transform.TransformPoint((Vector3)spline[k].Position);
                    Gizmos.DrawSphere(knotPos, 0.1f);
                }

                // 2. 디렉터님이 인스펙터에 추가한 Data Point의 실제 위치 렌더링!
                if (i < m_Widths.Count && m_Widths[i] != null)
                {
                    var widthData = m_Widths[i];
                    for (int j = 0; j < widthData.Count; j++)
                    {
                        var dataPoint = widthData[j];

                        // 사용자가 입력한 Index를 0.0 ~ 1.0 비율로 해석하여 위치를 찾음
                        float t = math.clamp(dataPoint.Index, 0f, 1f);

                        SplineUtility.Evaluate(spline, t, out var pos, out var _, out var _);
                        Vector3 worldPos = transform.TransformPoint(pos);

                        // 눈에 확 띄는 빨간색 구체! 여기가 바로 그 데이터가 적용되는 중심지입니다.
                        Gizmos.color = Color.red;
                        Gizmos.DrawSphere(worldPos, 0.4f);

                        // 허공에 텍스트를 띄워 어떤 데이터인지 명확히 안내
                        GUIStyle style = new GUIStyle();
                        style.normal.textColor = Color.black;
                        style.fontSize = 10;
                        style.fontStyle = FontStyle.Bold;

                        Handles.Label(worldPos + Vector3.up * 1f,
                            $"▼ [Data {j}]\n위치(Index): {t:F2}\n넓이(Value): {dataPoint.Value:F1}", style);
                    }
                }
            }
        }
#endif
    }
}