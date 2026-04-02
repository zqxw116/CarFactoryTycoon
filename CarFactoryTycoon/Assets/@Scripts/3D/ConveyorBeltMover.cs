using UnityEngine;

public class ConveyorBeltMover : MonoBehaviour
{
    [Header("벨트 이동 속도")]
    public float scrollSpeed = 0.5f;

    private Material targetMaterial;
    private Vector2 currentOffset;

    // Unity 6 / URP 환경에서 주로 사용하는 텍스처 속성 이름들
    private static readonly int BaseMap = Shader.PropertyToID("_BaseMap");
    private static readonly int MainTex = Shader.PropertyToID("_MainTex");

    void Start()
    {
        // MeshRenderer에서 머티리얼을 가져옵니다.
        var renderer = GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            targetMaterial = renderer.material;
        }
    }

    void Update()
    {
        if (targetMaterial == null) return;

        // 시간에 따라 오프셋 계산
        currentOffset.y += scrollSpeed * Time.deltaTime;

        // 1. 일반적인 쉐이더(Built-in) 대응
        if (targetMaterial.HasProperty(MainTex))
            targetMaterial.SetTextureOffset(MainTex, currentOffset);

        // 2. URP(Universal Render Pipeline) 쉐이더 대응
        if (targetMaterial.HasProperty(BaseMap))
            targetMaterial.SetTextureOffset(BaseMap, currentOffset);
    }
}