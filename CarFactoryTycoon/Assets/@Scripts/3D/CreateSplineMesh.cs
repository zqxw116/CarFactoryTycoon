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
        [SerializeField]
        List<SplineData<float>> m_Widths = new List<SplineData<float>>();

        public List<SplineData<float>> Widths
        {
            get
            {
                foreach (var width in m_Widths)
                {
                    if (width.DefaultValue == 0)
                        width.DefaultValue = 1f;
                }
                return m_Widths;
            }
        }

        [SerializeField] SplineContainer m_Spline;

        public SplineContainer Container
        {
            get
            {
                if (m_Spline == null) m_Spline = GetComponent<SplineContainer>();
                return m_Spline;
            }
            set => m_Spline = value;
        }

        [SerializeField] int m_SegmentsPerMeter = 1;
        [SerializeField] Mesh m_Mesh;
        [SerializeField] float m_TextureScale = 1f;

        // [수정됨] 머티리얼을 인스펙터에서 직접 넣을 수 있게 변경
        [Header("Material Settings")]
        [SerializeField] Material m_Material;

        public IReadOnlyList<Spline> splines => LoftSplines;

        public IReadOnlyList<Spline> LoftSplines
        {
            get
            {
                if (m_Spline == null) m_Spline = GetComponent<SplineContainer>();
                if (m_Spline == null) return null;
                return m_Spline.Splines;
            }
        }

        public Mesh LoftMesh
        {
            get
            {
                if (m_Mesh != null) return m_Mesh;

                m_Mesh = new Mesh();

                // [수정됨] Resources.Load 대신 인스펙터에 할당된 머티리얼 사용
                if (m_Material == null) m_Material = Resources.Load<Material>("Prefabs/Road1"); 
                GetComponent<MeshRenderer>().sharedMaterial = m_Material;

                return m_Mesh;
            }
        }

        public int SegmentsPerMeter => Mathf.Min(10, Mathf.Max(1, m_SegmentsPerMeter));

        List<Vector3> m_Positions = new List<Vector3>();
        List<Vector3> m_Normals = new List<Vector3>();
        List<Vector2> m_Textures = new List<Vector2>();
        List<int> m_Indices = new List<int>();
        bool m_LoftRoadsRequested = false;

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

            // [수정됨] 에셋으로 저장된 메쉬는 파괴하지 않도록 방어 코드 추가
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

        // ... (중간 이벤트 함수들은 기존과 동일하게 유지) ...
        void OnSplineContainerAdded(SplineContainer container, int index) { if (container == m_Spline) LoftAllRoads(); }
        void OnSplineContainerRemoved(SplineContainer container, int index) { if (container == m_Spline) LoftAllRoads(); }
        void OnSplineContainerReordered(SplineContainer container, int previousIndex, int newIndex) { if (container == m_Spline) LoftAllRoads(); }
        void OnAfterSplineWasModified(Spline s) { m_LoftRoadsRequested = true; }
        void OnSplineChanged(Spline spline, int knotIndex, SplineModification modification) { OnAfterSplineWasModified(spline); }
        void OnAfterSplineDataWasModified(SplineData<float> splineData) { m_LoftRoadsRequested = true; }


        public void LoftAllRoads()
        {
            LoftMesh.Clear();
            m_Positions.Clear();
            m_Normals.Clear();
            m_Textures.Clear();
            m_Indices.Clear();

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

                var w = 1f;
                if (widthDataIndex < m_Widths.Count && m_Widths[widthDataIndex] != null)
                {
                    w = m_Widths[widthDataIndex].DefaultValue;
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
        // =======================================================
        // [완성판] 메쉬 추출부터 완벽한 최적화 프리팹 생성까지 원클릭 자동화
        // =======================================================
        [ContextMenu("★★ 원클릭 최적화 프리팹 만들기 (Bake & Create Prefab)")]
        public void CreateOptimizedPrefab()
        {
            if (m_Mesh == null || m_Mesh.vertexCount == 0)
            {
                Debug.LogWarning("추출할 메쉬가 없습니다. 스플라인을 확인하세요.");
                return;
            }

            // 1. 프리팹 저장 경로 지정 창 띄우기
            string defaultName = gameObject.name + "_Conveyor";
            string path = EditorUtility.SaveFilePanelInProject(
                "컨베이어 프리팹 저장",
                defaultName,
                "prefab",
                "저장할 위치를 선택하세요. (동일한 폴더에 메쉬 데이터도 함께 생성됩니다)"
            );

            if (string.IsNullOrEmpty(path)) return;

            // 경로 문자열 정리 (.prefab 확장자를 기준으로 메쉬 경로 도출)
            string prefabPath = path;
            string meshPath = path.Replace(".prefab", ".asset");

            // 2. 메쉬 데이터(Asset) 파일로 영구 굽기
            Mesh bakedMesh = Instantiate(m_Mesh);
            bakedMesh.name = gameObject.name + "_Mesh";
            AssetDatabase.CreateAsset(bakedMesh, meshPath);

            // 3. 현재 씬에 있는 오브젝트 복제 (머티리얼, 스크립트 등 모든 세팅 보존)
            GameObject clone = Instantiate(gameObject);
            clone.name = gameObject.name + "_Optimized";

            // 4. 복제본의 메쉬를 방금 구워낸 영구 메쉬 파일로 교체
            clone.GetComponent<MeshFilter>().sharedMesh = bakedMesh;

            // 5. [핵심] 복제본에서 게임 성능을 갉아먹는 스플라인 연산 컴포넌트 완벽 제거
            DestroyImmediate(clone.GetComponent<CreateSplineMesh>());
            DestroyImmediate(clone.GetComponent<SplineContainer>());

            // 6. 찌꺼기가 제거된 순수 오브젝트를 프리팹(.prefab) 파일로 패키징
            PrefabUtility.SaveAsPrefabAsset(clone, prefabPath);

            // 7. 씬에 잠시 만들었던 임시 복제본 파괴 및 에셋 새로고침
            DestroyImmediate(clone);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"<color=cyan>[프리팹 자동화 성공!]</color>\n" +
                      $"1. 메쉬 저장: {meshPath}\n" +
                      $"2. 프리팹 완성: {prefabPath}\n" +
                      $"프로젝트 창을 확인해 보세요. 이제 이 프리팹을 꺼내 쓰시면 성능 저하가 0%입니다!");
        }
#endif
    }
}