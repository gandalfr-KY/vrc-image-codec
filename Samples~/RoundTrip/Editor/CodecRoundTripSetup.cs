#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UdonSharpEditor;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.Components;

namespace VrcImageCodec.Samples.Editor
{
public static class CodecRoundTripSetup
{
    [MenuItem("Tools/Image Codec/Create round-trip sample")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var selectedImage = Selection.activeObject as Texture;
        const string directory = "Assets/ImageCodecSample";
        Directory.CreateDirectory(directory);
        AvifEncoderSetup.EnsurePrograms(typeof(CodecRoundTrip), typeof(ImageCodecLink));
        var root = new GameObject("Codec round-trip sample");
        Undo.RegisterCreatedObjectUndo(root, "Create codec sample");
        var sample = root.AddUdonSharpComponent<CodecRoundTrip>();
        var linkObject = Rect("Run codec", root.transform, 64, 590, 300, 60).gameObject;
        linkObject.transform.SetParent(root.transform, false);
        sample.link = linkObject.AddUdonSharpComponent<ImageCodecLink>();
        var link = sample.link;
        ImageEncoderSetup.Create();
        link.encoder = Selection.activeGameObject.GetComponent<ImageEncoder>();
        link.encoder.transform.SetParent(root.transform, false);
        var input =
            new RenderTexture(640, 360, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        input.name = "Sample input";
        AssetDatabase.CreateAsset(
            input, AssetDatabase.GenerateUniqueAssetPath(directory + "/Input.renderTexture"));
        ImageEncoderSetup.Configure(link.encoder, input, ImageEncodingFormat.AVIF, 640, 360);
        ImageDecoderSetup.Create();
        link.decoder = Selection.activeGameObject.GetComponent<ImageDecoder>();
        link.decoder.transform.SetParent(root.transform, false);
        sample.sourceImage = selectedImage ? selectedImage : CreatePattern(directory);

        var camera = new GameObject("Sample view").AddComponent<Camera>();
        camera.transform.SetParent(root.transform, false);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(.06f, .07f, .09f);
        camera.depth = 100;
        if (!Object.FindObjectOfType<VRCSceneDescriptor>())
        {
            var world = root.AddComponent<VRCSceneDescriptor>();
            var spawn = new GameObject("Spawn").transform;
            spawn.SetParent(root.transform, false);
            world.spawns = new[] { spawn };
        }
        var canvasObject = new GameObject("Preview", typeof(RectTransform), typeof(Canvas),
                                          typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(root.transform, false);
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        var canvas = canvasObject.transform;
        Label("Image codec round trip", canvas, 64, 44, 1152, 46, 30);
        Label("Original", canvas, 64, 120, 548, 35, 22);
        Label("Decoded", canvas, 668, 120, 548, 35, 22);
        sample.original =
            Rect("Original image", canvas, 64, 168, 548, 308).gameObject.AddComponent<RawImage>();
        sample.original.texture = sample.sourceImage;
        link.display = Rect("Decoded image", canvas, 668, 168, 548, 308).gameObject.AddComponent<RawImage>();
        link.display.color = Color.white;
        sample.status = Label("Press Play, then Encode & Decode.", canvas, 64, 505, 1152, 55, 20);
        var button = link.gameObject;
        button.transform.SetParent(canvas, false);
        button.AddComponent<Image>().color = new Color(.18f, .28f, .38f);
        var runButton = button.AddComponent<Button>();
        Label("Encode & Decode", button.transform, 0, 0, 300, 60, 22).alignment = TextAnchor.MiddleCenter;
        ImageCodecLinkInspector.ConnectButton(runButton, link);
        Label("Image encoder → encoded bytes → Image decoder → preview", canvas, 400, 590, 816, 60, 18);
        if (!Object.FindObjectOfType<EventSystem>())
        {
            var events = new GameObject("Sample events", typeof(EventSystem), typeof(StandaloneInputModule));
            events.transform.SetParent(root.transform, false);
        }
        UdonSharpEditorUtility.CopyProxyToUdon(link);
        UdonSharpEditorUtility.CopyProxyToUdon(sample);
        EditorUtility.SetDirty(sample);
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = link.gameObject;
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float width,
                                      float height)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
        return rect;
    }

    private static Text Label(string text, Transform parent, float x, float y, float width, float height,
                              int fontSize)
    {
        var label = Rect(text, parent, x, y, width, height).gameObject.AddComponent<Text>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.fontSize = fontSize;
        label.color = new Color(.9f, .92f, .95f);
        label.text = text;
        label.raycastTarget = false;
        return label;
    }

    private static Texture2D CreatePattern(string directory)
    {
        var texture = new Texture2D(640, 360, TextureFormat.RGB24, false);
        var pixels = new Color[640 * 360];
        for (int y = 0; y < 360; y++)
            for (int x = 0; x < 640; x++)
                pixels[y * 640 + x] = new Color(x / 639f, y / 359f, ((x / 40 + y / 40) % 2) * .5f);
        texture.SetPixels(pixels);
        texture.Apply();
        AssetDatabase.CreateAsset(texture,
                                  AssetDatabase.GenerateUniqueAssetPath(directory + "/Pattern.asset"));
        return texture;
    }
}
}
#endif
