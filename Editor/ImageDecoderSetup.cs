#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharpEditor;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
public static class ImageDecoderSetup
{
    [MenuItem("Tools/Image Codec/Create decoder")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        AvifEncoderSetup.EnsurePrograms(typeof(ImageDecoder));
        var root = new GameObject("Image decoder");
        Undo.RegisterCreatedObjectUndo(root, "Create image decoder");
        var decoder = root.AddUdonSharpComponent<ImageDecoder>();
        AvifEncoderSetup.CreateDecoder();
        decoder.avifDecoder = Selection.activeGameObject.GetComponent<AvifDecoder>();
        decoder.avifDecoder.transform.SetParent(root.transform, false);
        decoder.avifDecoder.gameObject.hideFlags = HideFlags.HideInHierarchy;
        JpegCodecSetup.CreateDecoder();
        decoder.jpegDecoder = Selection.activeGameObject.GetComponent<JpegDecoder>();
        decoder.jpegDecoder.transform.SetParent(root.transform, false);
        decoder.jpegDecoder.gameObject.hideFlags = HideFlags.HideInHierarchy;
        UdonSharpEditorUtility.CopyProxyToUdon(decoder);
        EditorUtility.SetDirty(decoder);
        EditorSceneManager.MarkSceneDirty(root.scene);
        Selection.activeGameObject = root;
    }
}

[CustomEditor(typeof(ImageDecoder))]
public class ImageDecoderInspector : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawProgramSource(target, false)) return;
        UdonSharpGUI.DrawCompileErrorTextArea();
        EditorGUILayout.HelpBox(
            "Format and resolution are detected automatically. Use an Image Codec Link to connect " +
                "an encoder and display.",
            MessageType.Info);
        var decoder = (ImageDecoder)target;
        if (decoder.error != "") EditorGUILayout.HelpBox(decoder.error, MessageType.Error);
    }
}
}
#endif
