#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using VrcImageCodec;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;

[InitializeOnLoad]
public static class JpegCodecTestSetup
{
    const string Root = "Logs/JpegCodec";
    static JpegCodecTestSetup()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Root + "/version.txt", "jpeg-gpu-v2");
        EditorApplication.update += Tick;
    }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
        if (EditorApplication.isPlaying)
        {
            if (SessionState.GetBool("JpegCodec.running", false) &&
                (File.Exists(Root + "/done.txt") || File.Exists(Root + "/error.txt")))
                EditorApplication.isPlaying = false;
            return;
        }
        if (SessionState.GetBool("JpegCodec.running", false))
        {
            SessionState.SetBool("JpegCodec.running", false);
            ClientSimSettings.SaveSettings(
                JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString("JpegCodec.settings", "")));
            EditorSceneManager.OpenScene(SessionState.GetString("JpegCodec.scene", ""));
            File.WriteAllText(Root + "/stopped.txt", "ClientSim stopped; scene and settings restored.");
            return;
        }
        if (!File.Exists(Root + "/prepare.request")) return;
        File.Delete(Root + "/prepare.request");
        foreach (string file in new[] { "done.txt", "error.txt", "stopped.txt", "results.json" })
            File.Delete(Root + "/" + file);
        try
        {
            if (!string.IsNullOrEmpty(SceneManager.GetActiveScene().path))
                SessionState.SetString("JpegCodec.scene", SceneManager.GetActiveScene().path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var descriptor = new GameObject("ClientSim world").AddComponent<VRCSceneDescriptor>();
            descriptor.spawns = new[] { new GameObject("Spawn").transform };
            new GameObject("View").AddComponent<Camera>().tag = "MainCamera";
            JpegCodecSetup.Create();
            var codec = Selection.activeGameObject;
            var observer = new GameObject("JPEG test observer").AddComponent<JpegCodecObserver>();
            observer.encoder =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(codec.GetComponent<JpegEncoder>());
            observer.decoder =
                UdonSharpEditorUtility.GetBackingUdonBehaviour(codec.GetComponent<JpegDecoder>());
            new GameObject("Codec batch pacing").AddComponent<CodecBatchPacing>().root = Root;
            EditorSceneManager.SaveScene(scene, "Assets/JpegCodecTests/Tests.unity");
            SessionState.SetString("JpegCodec.settings", JsonUtility.ToJson(ClientSimSettings.Instance));
            var settings =
                JsonUtility.FromJson<ClientSimSettings>(JsonUtility.ToJson(ClientSimSettings.Instance));
            settings.enableClientSim = true;
            settings.spawnPlayer = false;
            settings.hideMenuOnLaunch = true;
            settings.targetFrameRate = 60;
            ClientSimSettings.SaveSettings(settings);
            SessionState.SetBool("JpegCodec.running", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            Debug.LogException(e);
            EditorSceneManager.OpenScene(
                SessionState.GetString("JpegCodec.scene", "Assets/ManualDemo/Installation.unity"));
        }
    }
}
#endif
