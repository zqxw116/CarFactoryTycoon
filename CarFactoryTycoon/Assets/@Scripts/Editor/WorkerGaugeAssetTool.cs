using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 작업자 컨디션 게이지 에셋(흰색 스프라이트 + 게이지 프리팹)을 만드는 에디터 툴.
///
/// - 흰색 스프라이트 1장을 배경/채움이 함께 쓴다 (텍스처가 같아야 배치가 합쳐진다.
///   내장 UISprite는 별도 텍스처라 쓰지 않는다. sprite가 없으면 Filled 타입이 동작하지 않는다)
/// - 모든 Image Raycast Target 끔
/// - 프리팹은 프리뷰 씬에서 조립해 저장하므로 열린 씬을 건드리지 않는다.
///
/// 사용법: Tools ▸ CarFactory ▸ 작업자 게이지 에셋 생성 (이미 있으면 덮어씀)
/// </summary>
public static class WorkerGaugeAssetTool
{
    private const string MENU_PATH = "Tools/CarFactory/작업자 게이지 에셋 생성";
    private const string SPRITE_PATH = "Assets/@Resource/Sprites/UI_White.png";
    private const string PREFAB_PATH = "Assets/@Resource/Prefabs/UI/WorkerGauge.prefab";
    private const string LOG_TAG = "[작업자 게이지]";

    // 프리팹 기본 크기(캔버스 로컬 단위). 런타임에 WorkerGaugeCanvas.gaugeSize로 덮어쓴다.
    private static readonly Vector2 DEFAULT_SIZE = new Vector2(80f, 12f);
    private const float FILL_PADDING = 1.5f;

    [MenuItem(MENU_PATH)]
    private static void Create()
    {
        Sprite sprite = CreateWhiteSprite();
        if (sprite == null)
        {
            Debug.LogError($"{LOG_TAG} 흰색 스프라이트 생성 실패: {SPRITE_PATH}");
            return;
        }
        CreatePrefab(sprite);
    }

    private static Sprite CreateWhiteSprite()
    {
        string fullPath = Path.Combine(Directory.GetCurrentDirectory(), SPRITE_PATH);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

        if (!File.Exists(fullPath))
        {
            var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var pixels = new Color32[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(255, 255, 255, 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(SPRITE_PATH, ImportAssetOptions.ForceSynchronousImport);
        }

        var importer = AssetImporter.GetAtPath(SPRITE_PATH) as TextureImporter;
        if (importer == null) return null;

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.mipmapEnabled = false;
        importer.alphaIsTransparency = true;
        importer.filterMode = FilterMode.Bilinear;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        var settings = new TextureImporterSettings();
        importer.ReadTextureSettings(settings);
        settings.spriteMeshType = SpriteMeshType.FullRect;
        importer.SetTextureSettings(settings);
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Sprite>(SPRITE_PATH);
    }

    private static void CreatePrefab(Sprite sprite)
    {
        string dir = Path.GetDirectoryName(PREFAB_PATH).Replace('\\', '/');
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), dir));
        AssetDatabase.Refresh();

        // 열린 씬을 건드리지 않도록 프리뷰 씬에서 조립
        Scene preview = EditorSceneManager.NewPreviewScene();
        try
        {
            var root = new GameObject("WorkerGauge", typeof(RectTransform));
            SceneManager.MoveGameObjectToScene(root, preview);
            var rootRt = (RectTransform)root.transform;
            rootRt.sizeDelta = DEFAULT_SIZE;
            rootRt.pivot = new Vector2(0.5f, 0.5f);

            var bg = root.AddComponent<Image>();
            bg.sprite = sprite;
            bg.type = Image.Type.Simple;
            bg.color = new Color(0f, 0f, 0f, 0.6f);
            bg.raycastTarget = false;

            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(root.transform, false);
            var fillRt = (RectTransform)fillGo.transform;
            fillRt.anchorMin = Vector2.zero;
            fillRt.anchorMax = Vector2.one;
            fillRt.offsetMin = new Vector2(FILL_PADDING, FILL_PADDING);
            fillRt.offsetMax = new Vector2(-FILL_PADDING, -FILL_PADDING);

            var fill = fillGo.AddComponent<Image>();
            fill.sprite = sprite;
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            fill.fillAmount = 1f;
            fill.color = Color.white;
            fill.raycastTarget = false;

            var gauge = root.AddComponent<WorkerGauge>();
            var so = new SerializedObject(gauge);
            so.FindProperty("fill").objectReferenceValue = fill;
            so.FindProperty("background").objectReferenceValue = bg;
            so.ApplyModifiedPropertiesWithoutUndo();

            PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH, out bool ok);
            if (ok) Debug.Log($"{LOG_TAG} 프리팹 생성: {PREFAB_PATH} (스프라이트: {SPRITE_PATH})");
            else Debug.LogError($"{LOG_TAG} 프리팹 저장 실패: {PREFAB_PATH}");
        }
        finally
        {
            EditorSceneManager.ClosePreviewScene(preview);
        }
    }
}
