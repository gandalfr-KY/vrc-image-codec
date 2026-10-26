#if UNITY_EDITOR
using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.SDKBase;
using VRC.Udon;

// Only orchestration, file output and validation readbacks are native C#.
// All measured conversion is performed by the production compiled Udon components.
[DefaultExecutionOrder(32000)]
public class CodecBenchmark : MonoBehaviour
{
    public UdonBehaviour[] encoders, decoders;
    public RenderTexture input;
    public string directory;
    public int width, height;
    public bool subsampleChroma;
    [Serializable]
    public class Measurement
    {
        public string id, codec, image, sampling, error;
        public int width, height, quality, bytes, encodeFrames, decodeFrames;
        public double encodeSeconds, decodeSeconds;
    }
    int job, phase, firstFrame, oldFrameRate, oldVsync;
    double startedAt, previousFrame;
    bool oldRunInBackground;
    Texture2D source;
    Measurement measurement;
    RenderTexture display;
    static readonly string[] Images = { "library", "bar", "lounge" };
    int Codec => job % 2;
    int Quality => 60 + job / 6 * 10;
    string Image => Images[job / 2 % 3];

    void Start()
    {
        foreach (var bridge in Resources
                     .FindObjectsOfTypeAll<VRC.SDK3.ClientSim.ClientSimUdonInputBehaviour>())
            if (bridge.gameObject.scene.IsValid()) bridge.enabled = false;
        oldFrameRate = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount;
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        oldRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        startedAt = previousFrame = Time.realtimeSinceStartupAsDouble;
    }

    void Begin()
    {
        if (source) Destroy(source);
        source = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
        if (!source.LoadImage(File.ReadAllBytes(Path.Combine(directory, "images", Image + ".png"))))
            throw new InvalidDataException("Cannot load fixture: " + Image);
        Graphics.Blit(source, input);
        measurement = new Measurement { codec = Codec == 0 ? "AVIF" : "JPEG",
                                        image = Image,
                                        width = width,
                                        height = height,
                                        quality = Quality,
                                        sampling = subsampleChroma ? "420" : "444" };
        measurement.id = Image + "-" + width + "x" + height + "-" + measurement.codec + "-" +
                         measurement.sampling + "-q" + Quality;
        encoders[Codec].SetProgramVariable("quality", Quality);
        startedAt = Time.realtimeSinceStartupAsDouble;
        firstFrame = Time.frameCount;
        phase = 1;
        encoders[Codec].SendCustomEvent("BeginEncoding");
    }

    void LateUpdate()
    {
        try
        {
            Advance();
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(directory, "error.txt"), exception.ToString());
            phase = 9;
        }
        if (phase == 9)
        {
            EditorApplication.isPlaying = false;
            return;
        }
        // Headless Editor has no Game view to enforce the requested 60 fps.
        if (!Application.isBatchMode) return;
        double remaining = 1.0 / 60 - (Time.realtimeSinceStartupAsDouble - previousFrame);
        if (remaining > 0) Thread.Sleep((int)Math.Ceiling(remaining * 1000));
        previousFrame = Time.realtimeSinceStartupAsDouble;
    }

    void Advance()
    {
        if (phase == 9 || phase == 3) return;
        if (phase == 0)
        {
            if (Time.realtimeSinceStartupAsDouble - startedAt > 30)
                throw new TimeoutException("ClientSim did not create a local player.");
            if (!Utilities.IsValid(Networking.LocalPlayer) ||
                Time.realtimeSinceStartupAsDouble - startedAt < 3)
                return;
            Begin();
            return;
        }
        if (Time.realtimeSinceStartupAsDouble - startedAt > 180)
            throw new TimeoutException(measurement.id + ", phase " + phase);
        if (phase == 1)
        {
            if ((bool)encoders[Codec].GetProgramVariable("busy")) return;
            measurement.encodeSeconds = Time.realtimeSinceStartupAsDouble - startedAt;
            measurement.encodeFrames = Time.frameCount - firstFrame;
            if (!(bool)encoders[Codec].GetProgramVariable("complete"))
            {
                measurement.error = (string)encoders[Codec].GetProgramVariable("error");
                Finish();
                return;
            }
            byte[] bytes =
                (byte[])encoders[Codec].GetProgramVariable(Codec == 0 ? "resultAvif" : "resultJpeg");
            measurement.bytes = bytes.Length;
            File.WriteAllBytes(Path.Combine(directory, measurement.id + (Codec == 0 ? ".avif" : ".jpg")),
                               bytes);
            decoders[Codec].SetProgramVariable(Codec == 0 ? "inputAvif" : "inputJpeg", bytes);
            startedAt = Time.realtimeSinceStartupAsDouble;
            firstFrame = Time.frameCount;
            phase = 2;
            decoders[Codec].SendCustomEvent("BeginDecoding");
            return;
        }
        if (phase != 2) return;
        if ((bool)decoders[Codec].GetProgramVariable("busy")) return;
        measurement.decodeSeconds = Time.realtimeSinceStartupAsDouble - startedAt;
        measurement.decodeFrames = Time.frameCount - firstFrame;
        if (!(bool)decoders[Codec].GetProgramVariable("complete"))
        {
            measurement.error = (string)decoders[Codec].GetProgramVariable("error");
            Finish();
            return;
        }
        // Validation runs after the measured completion fence; no validation work affects timing.
        var decoded = (RenderTexture)decoders[Codec].GetProgramVariable("resultTexture");
        phase = 3;
        SavePixels(decoded, Path.Combine(directory, measurement.id + ".rgba"),
                   () =>
                   {
                       string sourcePath =
                           Path.Combine(directory, Image + "-" + width + "x" + height + "-source.rgba");
                       if (File.Exists(sourcePath))
                       {
                           Finish();
                           return;
                       }
                       SavePixels(input, sourcePath, Finish);
                   });
    }

    void SavePixels(Texture texture, string path, Action finished)
    {
        display =
            new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        display.Create();
        Graphics.Blit(texture, display);
        AsyncGPUReadback.Request(display, 0, TextureFormat.RGBA32,
                                 request =>
                                 {
                                     display.Release();
                                     Destroy(display);
                                     if (request.hasError)
                                     {
                                         File.WriteAllText(Path.Combine(directory, "error.txt"),
                                                           "Validation readback failed: " + path);
                                         phase = 9;
                                         return;
                                     }
                                     File.WriteAllBytes(path, request.GetData<byte>().ToArray());
                                     finished();
                                 });
    }

    void Finish()
    {
        File.AppendAllText(Path.Combine(directory, "measurements.jsonl"),
                           JsonUtility.ToJson(measurement) + "\n");
        Debug.Log("CODEC_BENCHMARK " + JsonUtility.ToJson(measurement));
        job++;
        if (job < 18)
        {
            Begin();
            return;
        }
        File.WriteAllText(Path.Combine(directory, "group.done"), "done");
        phase = 9;
    }

    void OnDestroy()
    {
        if (source) Destroy(source);
        Application.runInBackground = oldRunInBackground;
        Application.targetFrameRate = oldFrameRate;
        QualitySettings.vSyncCount = oldVsync;
    }
}
#endif
