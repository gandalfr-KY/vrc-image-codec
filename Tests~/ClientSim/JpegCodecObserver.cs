#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Udon;
using VRC.SDKBase;

// Only observation and fixture loading are native; the codec runs as compiled Udon.
public class JpegCodecObserver : MonoBehaviour
{
    public UdonBehaviour encoder, decoder;
    const string Root = "Logs/JpegCodec";
    [Serializable]
    public class Job
    {
        public string id, path;
        public int width, height, quality;
        public bool linear;
        public bool subsampleChroma;
    }
    [Serializable]
    public class Jobs
    {
        public Job[] jobs;
    }
    [Serializable]
    public class Row
    {
        public string id;
        public int bytes;
        public double encodeSeconds, decodeSeconds;
    }
    [Serializable]
    public class Results
    {
        public string gpu;
        public Row[] rows;
        public bool cancelledAndReused, malformedRejected, invalidSettingsRejected;
    }
    readonly List<Row> rows = new List<Row>();
    Job[] jobs;
    Texture2D source;
    int job, phase, oldCap, oldVsync;
    double at;
    bool cancelled, decoderCancelled;
    byte[] jpeg;
    Row row;
    void Start()
    {
        jobs = JsonUtility.FromJson<Jobs>(File.ReadAllText(Root + "/jobs.json")).jobs;
        oldCap = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount;
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
                encoder.SetProgramVariable("quality", 0);
                encoder.SendCustomEvent("BeginEncoding");
                if ((bool)encoder.GetProgramVariable("busy") ||
                    (string)encoder.GetProgramVariable("error") == "")
                    throw new Exception("Invalid settings accepted");
                BeginJob();
                return;
            }
            if (Time.realtimeSinceStartupAsDouble - at > 120)
                throw new Exception("JPEG timed out at phase " + phase);
            if (phase == 5)
            {
                if ((bool)encoder.GetProgramVariable("readbackPending")) return;
                if ((bool)encoder.GetProgramVariable("complete"))
                    throw new Exception("Cancelled result published");
                cancelled = true;
                at = Time.realtimeSinceStartupAsDouble;
                encoder.SendCustomEvent("BeginEncoding");
                phase = 1;
                return;
            }
            if (phase == 1)
            {
                if ((bool)encoder.GetProgramVariable("busy")) return;
                if (!(bool)encoder.GetProgramVariable("complete"))
                    throw new Exception((string)encoder.GetProgramVariable("error"));
                jpeg = (byte[])encoder.GetProgramVariable("resultJpeg");
                row = new Row { id = jobs[job].id, bytes = jpeg.Length,
                                encodeSeconds = Time.realtimeSinceStartupAsDouble - at };
                File.WriteAllBytes(Root + "/" + row.id + ".jpg", jpeg);
                decoder.SetProgramVariable("inputJpeg", jpeg);
                at = Time.realtimeSinceStartupAsDouble;
                decoder.SendCustomEvent("BeginDecoding");
                if (!decoderCancelled)
                {
                    decoder.SendCustomEvent("CancelDecoding");
                    phase = 8;
                    return;
                }
                phase = 2;
                return;
            }
            if (phase == 2)
            {
                if ((bool)decoder.GetProgramVariable("busy")) return;
                if (!(bool)decoder.GetProgramVariable("complete"))
                    throw new Exception((string)decoder.GetProgramVariable("error"));
                row.decodeSeconds = Time.realtimeSinceStartupAsDouble - at;
                var texture = (RenderTexture)decoder.GetProgramVariable("resultTexture");
                RenderTexture preview = null;
                if (jobs[job].linear)
                {
                    preview = new RenderTexture(texture.width, texture.height, 0, RenderTextureFormat.ARGB32,
                                                RenderTextureReadWrite.sRGB);
                    preview.Create();
                    Graphics.Blit(texture, preview);
                }
                phase = 3;
                AsyncGPUReadback.Request(
                    preview ? preview : texture, 0, TextureFormat.RGBA32,
                    request =>
                    {
                        if (request.hasError)
                        {
                            File.WriteAllText(Root + "/error.txt", "Validation readback failed");
                            return;
                        }
                        var png =
                            new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
                        png.LoadRawTextureData(request.GetData<byte>());
                        png.Apply();
                        File.WriteAllBytes(Root + "/" + row.id + ".png", png.EncodeToPNG());
                        Destroy(png);
                        if (preview)
                        {
                            preview.Release();
                            Destroy(preview);
                        }
                        phase = 4;
                    });
                return;
            }
            if (phase == 3) return;
            if (phase == 4)
            {
                rows.Add(row);
                job++;
                File.WriteAllText(
                    Root + "/progress.json",
                    JsonUtility.ToJson(
                        new Results { gpu = SystemInfo.graphicsDeviceName, rows = rows.ToArray() }, true));
                if (job < jobs.Length)
                {
                    BeginJob();
                    return;
                }
                byte[] broken = (byte[])jpeg.Clone();
                broken[0] = 0;
                decoder.SetProgramVariable("inputJpeg", broken);
                decoder.SendCustomEvent("BeginDecoding");
                at = Time.realtimeSinceStartupAsDouble;
                phase = 6;
                return;
            }
            if (phase == 6)
            {
                if ((bool)decoder.GetProgramVariable("busy")) return;
                if ((bool)decoder.GetProgramVariable("complete") ||
                    (string)decoder.GetProgramVariable("error") == "")
                    throw new Exception("Malformed JPEG accepted");
                // Valid header but invalid Huffman/marker data must also fail.
                byte[] broken = (byte[])jpeg.Clone();
                for (int k = 629; k < broken.Length - 2; k++)
                    broken[k] = 0;
                decoder.SetProgramVariable("inputJpeg", broken);
                decoder.SendCustomEvent("BeginDecoding");
                at = Time.realtimeSinceStartupAsDouble;
                phase = 7;
                return;
            }
            if (phase == 7)
            {
                if ((bool)decoder.GetProgramVariable("busy")) return;
                if ((bool)decoder.GetProgramVariable("complete") ||
                    (string)decoder.GetProgramVariable("error") == "")
                    throw new Exception("Malformed entropy accepted");
                File.WriteAllText(
                    Root + "/results.json",
                    JsonUtility.ToJson(
                        new Results { gpu = SystemInfo.graphicsDeviceName, rows = rows.ToArray(),
                                      cancelledAndReused = cancelled && decoderCancelled,
                                      malformedRejected = true, invalidSettingsRejected = true },
                        true));
                File.WriteAllText(Root + "/done.txt", "pass");
                phase = 9;
            }
            if (phase == 8)
            {
                if ((bool)decoder.GetProgramVariable("readbackPending")) return;
                if ((bool)decoder.GetProgramVariable("complete"))
                    throw new Exception("Cancelled decoder result published");
                decoderCancelled = true;
                at = Time.realtimeSinceStartupAsDouble;
                decoder.SendCustomEvent("BeginDecoding");
                phase = 2;
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            phase = 9;
        }
    }
    void BeginJob()
    {
        if (source) Destroy(source);
        source = new Texture2D(2, 2, TextureFormat.RGBA32, false, !jobs[job].linear);
        source.LoadImage(File.ReadAllBytes(jobs[job].path));
        encoder.SetProgramVariable("inputTexture", source);
        encoder.SetProgramVariable("width", jobs[job].width);
        encoder.SetProgramVariable("height", jobs[job].height);
        encoder.SetProgramVariable("quality", jobs[job].quality);
        encoder.SetProgramVariable("linearInput", jobs[job].linear);
        encoder.SetProgramVariable("subsampleChroma", jobs[job].subsampleChroma);
        decoder.SetProgramVariable("linearOutput", jobs[job].linear);
        at = Time.realtimeSinceStartupAsDouble;
        phase = 1;
        encoder.SendCustomEvent("BeginEncoding");
        if (!cancelled)
        {
            encoder.SendCustomEvent("CancelEncoding");
            phase = 5;
        }
    }
    void OnDestroy()
    {
        Application.targetFrameRate = oldCap;
        QualitySettings.vSyncCount = oldVsync;
    }
}
#endif
