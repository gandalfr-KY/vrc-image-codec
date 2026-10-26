#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.UI;
using UdonSharpEditor;
using VrcImageCodec;

namespace VrcAvif.Encoder.Editor
{
[CustomEditor(typeof(ImageCodecLink))]
public class ImageCodecLinkInspector : UnityEditor.Editor
{
    public override void OnInspectorGUI()
    {
        if (UdonSharpGUI.DrawProgramSource(target, false)) return;
        UdonSharpGUI.DrawCompileErrorTextArea();
        var link = (ImageCodecLink)target;
        serializedObject.Update();
        using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
        {
            EditorGUILayout.PropertyField(serializedObject.FindProperty("encoder"),
                                          new GUIContent("Encoder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("decoder"),
                                          new GUIContent("Decoder"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("display"),
                                          new GUIContent("Display"));
        }
        if (serializedObject.ApplyModifiedProperties() && !EditorApplication.isPlayingOrWillChangePlaymode)
            UdonSharpEditorUtility.CopyProxyToUdon(link);
        string error = link.ConfigurationError();
        if (error != "")
        {
            EditorGUILayout.HelpBox(error, MessageType.Info);
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        ConnectButton(link.GetComponent<Button>(), link);
        EditorGUILayout.HelpBox(
            "Encoded results are passed to the decoder automatically. " +
                "Place this component on a UI Button to run it with that button, or on a world object " +
                "with a collider to run it with Use.",
            MessageType.Info);
    }

    public static void ConnectButton(Button button, ImageCodecLink link)
    {
        if (!button) return;
        var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(link);
        for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            if (button.onClick.GetPersistentTarget(i) == backing &&
                button.onClick.GetPersistentMethodName(i) == "SendCustomEvent")
                return;
        Undo.RecordObject(button, "Connect codec button");
        UnityEventTools.AddStringPersistentListener(button.onClick, backing.SendCustomEvent, "Begin");
        EditorUtility.SetDirty(button);
    }

    [MenuItem("Tools/Image Codec/Create codec link")]
    public static void Create()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        AvifEncoderSetup.EnsurePrograms(typeof(ImageCodecLink));
        var root = new GameObject("Image codec link");
        Undo.RegisterCreatedObjectUndo(root, "Create codec link");
        root.AddUdonSharpComponent<ImageCodecLink>();
        Selection.activeGameObject = root;
    }
}
}
#endif
