#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UdonSharpEditor;
using TernaryStars;
using VrcAvif.Encoder.Editor;
using VRC.SDK3.ClientSim;
using VRC.SDK3.Components;

[InitializeOnLoad]
public static class CodecRealtimeSetup
{
    const string Root = "Logs/CodecRealtime";
    const string Config = "CodecRealtime.oldSettings";
    static CodecRealtimeSetup() { EditorApplication.update += Tick; }
    static void Tick()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
        if (EditorApplication.isPlaying)
        {
            if (File.Exists(Root + "/done.txt") || File.Exists(Root + "/error.txt")) EditorApplication.isPlaying = false;
            return;
        }
        if (SessionState.GetBool("CodecRealtime.running", false))
        {
            SessionState.SetBool("CodecRealtime.running", false);
            ClientSimSettings.SaveSettings(JsonUtility.FromJson<ClientSimSettings>(SessionState.GetString(Config, "")));
            File.WriteAllText(Root + "/stopped.txt", "ClientSim stopped; settings restored.");
            string previous = SessionState.GetString("CodecRealtime.oldScene", "");
            if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous);
            return;
        }
        if (!File.Exists(Root + "/prepare.request")) return;
        File.Delete(Root + "/prepare.request");
        foreach (string file in new[] { "done.txt", "error.txt", "stopped.txt", "results.json", "progress.txt", "input-disabled.txt" }) File.Delete(Root + "/" + file);
        try
        {
            SessionState.SetString("CodecRealtime.oldScene", SceneManager.GetActiveScene().path);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var descriptor = new GameObject("ClientSim world").AddComponent<VRCSceneDescriptor>();
            var spawn = new GameObject("Spawn"); spawn.transform.position = new Vector3(0, .1f, 0);
            descriptor.spawns = new[] { spawn.transform };
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "Floor"; floor.transform.position = new Vector3(0, -.1f, 0); floor.transform.localScale = new Vector3(10, .2f, 10);
            var light = new GameObject("Light").AddComponent<Light>(); light.type = LightType.Directional; light.transform.rotation = Quaternion.Euler(50, -30, 0);
            var view = new GameObject("Fixed test camera").AddComponent<Camera>(); view.transform.position = new Vector3(0, 1.6f, 0); view.tag = "MainCamera";
            AvifEncoderSetup.Create();
            var encoder = Selection.activeGameObject.GetComponent<TernaryAvifEncoder>();
            AvifEncoderSetup.Configure(encoder, 1920, 1080, 60);
            AvifEncoderSetup.CreateDecoder();
            var decoder = Selection.activeGameObject.GetComponent<TernaryAvifDecoder>();
            AvifEncoderSetup.Configure(decoder);
            var screen = GameObject.CreatePrimitive(PrimitiveType.Quad); screen.name = "Decoded image"; screen.transform.position = new Vector3(0, 1.6f, 3); screen.transform.localScale = new Vector3(3.2f, 1.8f, 1);
            screen.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Texture"));
            var monitor = new GameObject("Test observer").AddComponent<CodecRealtimeMonitor>();
            monitor.encoder = UdonSharpEditorUtility.GetBackingUdonBehaviour(encoder);
            monitor.gpu = UdonSharpEditorUtility.GetBackingUdonBehaviour(encoder.gpuTransform);
            monitor.decoder = UdonSharpEditorUtility.GetBackingUdonBehaviour(decoder);
            monitor.source = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/CodecRealtime/Source.png");
            monitor.input = encoder.gpuTransform.inputTexture;
            monitor.screen = screen.GetComponent<Renderer>();
            if (!monitor.source) throw new Exception("Missing source photo");
            UdonSharpEditorUtility.CopyProxyToUdon(encoder.gpuTransform);
            UdonSharpEditorUtility.CopyProxyToUdon(encoder);
            UdonSharpEditorUtility.CopyProxyToUdon(decoder);
            EditorSceneManager.SaveScene(scene, "Assets/CodecRealtime/Realtime.unity");
            SessionState.SetString(Config, JsonUtility.ToJson(ClientSimSettings.Instance));
            var settings = JsonUtility.FromJson<ClientSimSettings>(JsonUtility.ToJson(ClientSimSettings.Instance));
            settings.enableClientSim = true; settings.spawnPlayer = false; settings.hideMenuOnLaunch = true; settings.targetFrameRate = 60;
            ClientSimSettings.SaveSettings(settings);
            SessionState.SetBool("CodecRealtime.running", true);
            EditorApplication.isPlaying = true;
        }
        catch (Exception e) { File.WriteAllText(Root + "/error.txt", e.ToString()); Debug.LogException(e); }
    }
}
#endif
