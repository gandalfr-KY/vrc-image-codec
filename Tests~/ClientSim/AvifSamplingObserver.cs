#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Udon;
using VRC.SDKBase;

public class AvifSamplingObserver : MonoBehaviour
{
    public UdonBehaviour[] encoders, transforms, decoders;
    public RenderTexture[] inputs, packets, reconstructed;
    const string Root = "Logs/AvifSampling";
    [Serializable]
    public class Row
    {
        public string id;
        public int bytes;
        public double encodeSeconds, decodeSeconds, decodeCpuSeconds, decodeMaxStepMs;
    }
    [Serializable]
    public class Results
    {
        public Row[] rows;
    }
    readonly List<Row> rows = new List<Row>();
    Texture2D source;
    int job, phase, pending;
    double at;
    Row row;
    void Start()
    {
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        Application.runInBackground = true;
        at = Time.realtimeSinceStartupAsDouble;
    }
    void LateUpdate()
    {
        if (phase == 9) return;
        try
        {
            foreach (var bridge in Resources
                         .FindObjectsOfTypeAll<VRC.SDK3.ClientSim.ClientSimUdonInputBehaviour>())
                if (bridge.gameObject.scene.IsValid()) bridge.enabled = false;
            if (phase == 0)
            {
                if (!Utilities.IsValid(Networking.LocalPlayer) || Time.realtimeSinceStartupAsDouble - at < 3)
                    return;
                BeginJob();
                return;
            }
            if (Time.realtimeSinceStartupAsDouble - at > 120)
                throw new Exception("AVIF sampling timeout phase " + phase);
            if (phase == 1)
            {
                if ((bool)encoders[job].GetProgramVariable("busy")) return;
                if (!(bool)encoders[job].GetProgramVariable("complete"))
                    throw new Exception((string)encoders[job].GetProgramVariable("error"));
                byte[] avif = (byte[])encoders[job].GetProgramVariable("resultAvif");
                row = new Row { id = (job % 2 == 0 ? "420-" : "444-") + (job < 2 ? "small" : "fhd"),
                                bytes = avif.Length, encodeSeconds = Time.realtimeSinceStartupAsDouble - at };
                File.WriteAllBytes(Root + "/" + row.id + ".avif", avif);
                pending = 2;
                phase = 2;
                SaveRaw(packets[job], Root + "/" + row.id + ".packet.rgba");
                SaveRaw(reconstructed[job], Root + "/" + row.id + ".recon.rgba");
                decoders[job].SetProgramVariable("inputAvif", avif);
                at = Time.realtimeSinceStartupAsDouble;
                decoders[job].SendCustomEvent("BeginDecoding");
                return;
            }
            if (phase == 2)
            {
                if ((bool)decoders[job].GetProgramVariable("busy")) return;
                if (!(bool)decoders[job].GetProgramVariable("complete"))
                    throw new Exception((string)decoders[job].GetProgramVariable("error"));
                row.decodeSeconds = Time.realtimeSinceStartupAsDouble - at;
                row.decodeCpuSeconds = (float)decoders[job].GetProgramVariable("cpuSeconds");
                row.decodeMaxStepMs = (float)decoders[job].GetProgramVariable("maxStepMs");
                pending++;
                SaveRaw((RenderTexture)decoders[job].GetProgramVariable("filtered"),
                        Root + "/" + row.id + ".decoded.rgba");
                var output = (RenderTexture)decoders[job].GetProgramVariable("resultTexture");
                phase = 3;
                pending++;
                AsyncGPUReadback.Request(
                    output, 0, TextureFormat.RGBA32,
                    request =>
                    {
                        if (request.hasError)
                        {
                            File.WriteAllText(Root + "/error.txt", "RGB readback failed");
                            return;
                        }
                        var image =
                            new Texture2D(output.width, output.height, TextureFormat.RGBA32, false, true);
                        image.LoadRawTextureData(request.GetData<byte>());
                        image.Apply();
                        File.WriteAllBytes(Root + "/" + row.id + ".png", image.EncodeToPNG());
                        Destroy(image);
                        pending--;
                    });
                return;
            }
            if (phase != 3 || pending != 0) return;
            rows.Add(row);
            job++;
            File.WriteAllText(Root + "/results.json",
                              JsonUtility.ToJson(new Results { rows = rows.ToArray() }, true));
            if (job < encoders.Length)
            {
                BeginJob();
                return;
            }
            File.WriteAllText(Root + "/done.txt", "pass");
            phase = 9;
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            phase = 9;
        }
    }
    void SaveRaw(RenderTexture texture, string path)
    {
        AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32,
                                 request =>
                                 {
                                     if (request.hasError)
                                     {
                                         File.WriteAllText(Root + "/error.txt", "Fixture readback failed");
                                         return;
                                     }
                                     File.WriteAllBytes(path, request.GetData<byte>().ToArray());
                                     pending--;
                                 });
    }
    void BeginJob()
    {
        if (source) Destroy(source);
        source = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        source.LoadImage(File.ReadAllBytes(Root + (job < 2 ? "/small.png" : "/source.png")));
        Graphics.Blit(source, inputs[job]);
        at = Time.realtimeSinceStartupAsDouble;
        phase = 1;
        encoders[job].SendCustomEvent("BeginEncoding");
    }
}
#endif
