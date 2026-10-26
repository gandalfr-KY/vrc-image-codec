#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using VRC.Udon;
using VRC.SDKBase;

public class AvifDecoderObserver : MonoBehaviour
{
    const string Root = "Logs/AvifDecoder";
    public UdonBehaviour decoder;
    [Serializable]
    public class Job
    {
        public string id, path;
    }
    [Serializable]
    public class Jobs
    {
        public Job[] jobs;
        public string[] invalid;
    }
    [Serializable]
    public class Row
    {
        public string id;
        public int width, height, bytes, frames;
        public bool subsample;
        public double seconds, cpuSeconds, maxStepMs;
    }
    [Serializable]
    public class Results
    {
        public string gpu;
        public Row[] rows;
        public bool cancelledAndReused;
        public int invalidRejected;
    }
    readonly List<Row> rows = new List<Row>();
    Jobs suite;
    int job, phase, invalid;
    bool cancelled;
    double at;
    int beginFrame;
    Row row;
    void Start()
    {
        suite = JsonUtility.FromJson<Jobs>(File.ReadAllText(Root + "/jobs.json"));
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        Application.runInBackground = true;
        at = Time.realtimeSinceStartupAsDouble;
    }
    void Begin()
    {
        decoder.SetProgramVariable("inputAvif", File.ReadAllBytes(suite.jobs[job].path));
        at = Time.realtimeSinceStartupAsDouble;
        beginFrame = Time.frameCount;
        decoder.SendCustomEvent("BeginDecoding");
        phase = 1;
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
                Begin();
                return;
            }
            if (Time.realtimeSinceStartupAsDouble - at > 240)
                throw new Exception("Decoder timeout, phase " + phase + ", job " + job);
            if (phase == 1)
            {
                if (!cancelled && (bool)decoder.GetProgramVariable("readbackPending"))
                {
                    decoder.SendCustomEvent("CancelDecoding");
                    if ((bool)decoder.GetProgramVariable("busy") ||
                        (bool)decoder.GetProgramVariable("complete"))
                        throw new Exception("Cancellation published a result");
                    phase = 2;
                    return;
                }
                if ((bool)decoder.GetProgramVariable("busy")) return;
                if (!(bool)decoder.GetProgramVariable("complete"))
                    throw new Exception((string)decoder.GetProgramVariable("error"));
                if (!cancelled) throw new Exception("Readback cancellation was not exercised");
                row = new Row { frames = Time.frameCount - beginFrame,
                                id = suite.jobs[job].id,
                                width = (int)decoder.GetProgramVariable("width"),
                                height = (int)decoder.GetProgramVariable("height"),
                                bytes = (int)new FileInfo(suite.jobs[job].path).Length,
                                subsample = (bool)decoder.GetProgramVariable("subsampleChroma"),
                                seconds = Time.realtimeSinceStartupAsDouble - at,
                                cpuSeconds = (float)decoder.GetProgramVariable("cpuSeconds"),
                                maxStepMs = (float)decoder.GetProgramVariable("maxStepMs") };
                var filtered = (RenderTexture)decoder.GetProgramVariable("filtered");
                phase = 3;
                AsyncGPUReadback.Request(
                    filtered, 0, TextureFormat.RGBA32,
                    request =>
                    {
                        if (request.hasError)
                        {
                            File.WriteAllText(Root + "/error.txt", "YUV verification readback failed");
                            return;
                        }
                        File.WriteAllBytes(Root + "/" + row.id + ".rgba", request.GetData<byte>().ToArray());
                        rows.Add(row);
                        File.WriteAllText(
                            Root + "/results.json",
                            JsonUtility.ToJson(
                                new Results { gpu = SystemInfo.graphicsDeviceName, rows = rows.ToArray(),
                                              cancelledAndReused = cancelled, invalidRejected = invalid },
                                true));
                        phase = 4;
                    });
                return;
            }
            if (phase == 2)
            {
                if ((bool)decoder.GetProgramVariable("readbackPending")) return;
                cancelled = true;
                Begin();
                return;
            }
            if (phase == 3) return;
            if (phase == 4)
            {
                job++;
                if (job < suite.jobs.Length)
                {
                    Begin();
                    return;
                }
                BeginInvalid();
                return;
            }
            if (phase == 5)
            {
                if ((bool)decoder.GetProgramVariable("busy")) return;
                if ((bool)decoder.GetProgramVariable("complete") ||
                    (string)decoder.GetProgramVariable("error") == "")
                    throw new Exception("Invalid input accepted: " + suite.invalid[invalid]);
                invalid++;
                if (invalid < suite.invalid.Length)
                {
                    BeginInvalid();
                    return;
                }
                File.WriteAllText(
                    Root + "/results.json",
                    JsonUtility.ToJson(new Results { gpu = SystemInfo.graphicsDeviceName,
                                                     rows = rows.ToArray(), cancelledAndReused = cancelled,
                                                     invalidRejected = invalid },
                                       true));
                File.WriteAllText(Root + "/done.txt", "pass");
                phase = 9;
            }
        }
        catch (Exception e)
        {
            File.WriteAllText(Root + "/error.txt", e.ToString());
            phase = 9;
        }
    }
    void BeginInvalid()
    {
        decoder.SetProgramVariable("inputAvif", File.ReadAllBytes(suite.invalid[invalid]));
        at = Time.realtimeSinceStartupAsDouble;
        beginFrame = Time.frameCount;
        decoder.SendCustomEvent("BeginDecoding");
        phase = 5;
    }
}
#endif
