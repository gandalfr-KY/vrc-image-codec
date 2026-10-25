#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;
using VRC.Udon;
using VRC.SDKBase;

// Test-only observer. The codec executes as compiled Udon with its normal Update callbacks.
[DefaultExecutionOrder(32000)]
public class CodecRealtimeMonitor : MonoBehaviour
{
    public UdonBehaviour encoder, gpu, decoder;
    public Texture2D source;
    public RenderTexture input;
    public Renderer screen;
    const string Root = "Logs/CodecRealtime";
    [Serializable] class Row
    {
        public int requestedFps, budgetMs, bytes, baselineFrames, encodeFrames, decodeFrames;
        public double baselineFps, encodeSeconds, decodeSeconds, encodeMeanFrameMs, encodeP95FrameMs, encodeMaxFrameMs, decodeMeanFrameMs, decodeP95FrameMs, decodeMaxFrameMs;
        public float encodeCpuSeconds, encodeMaxStepMs, gpuSubmitSeconds, gpuMaxStepMs, decodeCpuSeconds, decodeMaxStepMs;
        public long allocatedBefore, allocatedPeak, reservedPeak;
        public bool byteIdentical, clientSimPlayerPresent;
    }
    [Serializable] class Results { public string gpu, cpu; public Row[] rows; }
    readonly List<Row> rows = new List<Row>();
    readonly List<float> frames = new List<float>();
    readonly int[] fps = { 60, 60, 30, 30, 90, 90 };
    readonly int[] budgets = { 2, 4, 2, 4, 2, 4 };
    Row row;
    byte[] reference;
    int job, phase, oldCap, oldVsync;
    double at;
    float nextMemorySample;
    void Start()
    {
        oldCap = Application.targetFrameRate; oldVsync = QualitySettings.vSyncCount;
        QualitySettings.vSyncCount = 0; Application.runInBackground = true;
        at = Time.realtimeSinceStartupAsDouble;
        Directory.CreateDirectory(Root);
    }
    void LateUpdate()
    {
        if (phase == 9) return;
        try
        {
            if (phase == 0)
            {
                // No player controls are needed. This project uses the legacy input backend.
                foreach (var inputBridge in Resources.FindObjectsOfTypeAll<VRC.SDK3.ClientSim.ClientSimUdonInputBehaviour>())
                    if (inputBridge.gameObject.scene.IsValid())
                    {
                        inputBridge.enabled = false;
                        File.WriteAllText(Root + "/input-disabled.txt", inputBridge.name + " enabled=" + inputBridge.enabled);
                    }
                if (!Utilities.IsValid(Networking.LocalPlayer) || Time.realtimeSinceStartupAsDouble - at < 3) return;
                Next(); return;
            }
            float elapsed = (float)(Time.realtimeSinceStartupAsDouble - at);
            frames.Add(Time.unscaledDeltaTime * 1000);
            if (Time.realtimeSinceStartup >= nextMemorySample)
            {
                row.allocatedPeak = Math.Max(row.allocatedPeak, Profiler.GetTotalAllocatedMemoryLong());
                row.reservedPeak = Math.Max(row.reservedPeak, Profiler.GetTotalReservedMemoryLong());
                nextMemorySample = Time.realtimeSinceStartup + .5f;
            }
            if (elapsed > 180) throw new Exception("Codec stage exceeded 180 seconds");
            if (phase == 1)
            {
                if (elapsed < 3) return;
                row.baselineFrames = frames.Count;
                row.baselineFps = frames.Count / elapsed;
                Graphics.Blit(source, input);
                encoder.SetProgramVariable("frameBudgetMs", (float)row.budgetMs);
                gpu.SetProgramVariable("diagonalsPerFrame", row.budgetMs == 2 ? 4 : 8);
                decoder.SetProgramVariable("frameBudgetMs", (float)row.budgetMs);
                decoder.SetProgramVariable("diagonalsPerFrame", row.budgetMs == 2 ? 8 : 16);
                at = Time.realtimeSinceStartupAsDouble; frames.Clear(); phase = 2;
                encoder.SendCustomEvent("BeginEncoding");
                Progress("encoding"); return;
            }
            if (phase == 2)
            {
                if ((bool)encoder.GetProgramVariable("busy")) return;
                if (!(bool)encoder.GetProgramVariable("complete")) throw new Exception("Encode: " + encoder.GetProgramVariable("error"));
                row.encodeSeconds = Time.realtimeSinceStartupAsDouble - at;
                row.encodeFrames = frames.Count;
                Stats(true);
                row.encodeCpuSeconds = (float)encoder.GetProgramVariable("cpuSeconds");
                row.encodeMaxStepMs = (float)encoder.GetProgramVariable("maxStepMs");
                row.gpuSubmitSeconds = (float)gpu.GetProgramVariable("submitCpuSeconds");
                row.gpuMaxStepMs = (float)gpu.GetProgramVariable("maxStepMs");
                var bytes = (byte[])encoder.GetProgramVariable("resultAvif");
                row.bytes = bytes.Length;
                if (reference == null) reference = bytes;
                row.byteIdentical = reference.Length == bytes.Length;
                for (int i = 0; row.byteIdentical && i < bytes.Length; i++) row.byteIdentical = reference[i] == bytes[i];
                if (!row.byteIdentical) throw new Exception("Scheduling changed AVIF output");
                File.WriteAllBytes(Root + "/" + job + ".avif", bytes);
                decoder.SetProgramVariable("inputAvif", bytes);
                at = Time.realtimeSinceStartupAsDouble; frames.Clear(); phase = 3;
                decoder.SendCustomEvent("BeginDecoding");
                Progress("decoding"); return;
            }
            if ((bool)decoder.GetProgramVariable("busy")) return;
            if (!(bool)decoder.GetProgramVariable("complete")) throw new Exception("Decode: " + decoder.GetProgramVariable("error"));
            row.decodeSeconds = Time.realtimeSinceStartupAsDouble - at;
            row.decodeFrames = frames.Count;
            Stats(false);
            row.decodeCpuSeconds = (float)decoder.GetProgramVariable("cpuSeconds");
            row.decodeMaxStepMs = (float)decoder.GetProgramVariable("maxStepMs");
            // Validation-only readback is outside the measured decode interval.
            var filtered = (RenderTexture)decoder.GetProgramVariable("filtered");
            var previous = RenderTexture.active; RenderTexture.active = filtered;
            var pixels = new Texture2D(filtered.width, filtered.height, TextureFormat.RGBA32, false, true);
            pixels.ReadPixels(new Rect(0, 0, filtered.width, filtered.height), 0, 0); pixels.Apply();
            File.WriteAllBytes(Root + "/" + job + ".atlas.rgba", pixels.GetRawTextureData<byte>().ToArray());
            Destroy(pixels); RenderTexture.active = previous;
            var output = (RenderTexture)decoder.GetProgramVariable("resultTexture");
            screen.material.mainTexture = output;
            RenderTexture.active = output;
            pixels = new Texture2D(output.width, output.height, TextureFormat.RGBA32, false);
            pixels.ReadPixels(new Rect(0, 0, output.width, output.height), 0, 0); pixels.Apply();
            File.WriteAllBytes(Root + "/" + job + ".png", pixels.EncodeToPNG());
            Destroy(pixels); RenderTexture.active = previous;
            rows.Add(row);
            File.WriteAllText(Root + "/results.json", JsonUtility.ToJson(new Results { gpu = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType, rows = rows.ToArray() }, true));
            job++;
            if (job == fps.Length)
            {
                phase = 9; File.WriteAllText(Root + "/done.txt", "PASS 6 ClientSim encode/decode cases");
                return;
            }
            Next();
        }
        catch (Exception e) { phase = 9; File.WriteAllText(Root + "/error.txt", e.ToString()); Debug.LogException(e); }
    }
    void Next()
    {
        Application.targetFrameRate = fps[job];
        row = new Row { requestedFps = fps[job], budgetMs = budgets[job], clientSimPlayerPresent = Utilities.IsValid(Networking.LocalPlayer), allocatedBefore = Profiler.GetTotalAllocatedMemoryLong() };
        phase = 1; at = Time.realtimeSinceStartupAsDouble; frames.Clear(); Progress("baseline");
    }
    void Stats(bool encode)
    {
        double sum = 0;
        foreach (float value in frames) sum += value;
        frames.Sort();
        double p95 = frames[(int)((frames.Count - 1) * .95)], max = frames[frames.Count - 1];
        if (encode) { row.encodeMeanFrameMs = sum / frames.Count; row.encodeP95FrameMs = p95; row.encodeMaxFrameMs = max; }
        else { row.decodeMeanFrameMs = sum / frames.Count; row.decodeP95FrameMs = p95; row.decodeMaxFrameMs = max; }
    }
    void Progress(string state) { File.WriteAllText(Root + "/progress.txt", job + "/6 " + row.requestedFps + "fps " + row.budgetMs + "ms " + state); }
    void OnDestroy() { Application.targetFrameRate = oldCap; QualitySettings.vSyncCount = oldVsync; }
}
#endif
