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
        var settings = new SerializedObject(decoder);
        AvifEncoderSetup.CreateDecoder();
        var avif = Selection.activeGameObject.GetComponent<AvifDecoder>();
        avif.transform.SetParent(root.transform, false);
        avif.gameObject.hideFlags = HideFlags.HideInHierarchy;
        settings.FindProperty("avifDecoder").objectReferenceValue = avif;
        JpegCodecSetup.CreateDecoder();
        var jpeg = Selection.activeGameObject.GetComponent<JpegDecoder>();
        jpeg.transform.SetParent(root.transform, false);
        jpeg.gameObject.hideFlags = HideFlags.HideInHierarchy;
        settings.FindProperty("jpegDecoder").objectReferenceValue = jpeg;
        settings.ApplyModifiedProperties();
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
            "Call TryBeginDecoding(bytes) from UdonSharp. Format and resolution are detected " +
                "automatically. The optional round-trip sample demonstrates data transfer and display.",
            MessageType.Info);
        var decoder = (ImageDecoder)target;
        if (decoder.LastError != "") EditorGUILayout.HelpBox(decoder.LastError, MessageType.Error);
    }
}
}
#endif
