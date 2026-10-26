#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;
using VRC.Udon;
using VRC.SDKBase;

// Native observer only schedules inputs and checks outputs; production code executes as Udon.
[DefaultExecutionOrder(32000)]
public class CodecEncoderObserver : MonoBehaviour
{
    public UdonBehaviour[] drivers, backends;
    public UdonBehaviour capture;
    public RenderTexture input;
    public Texture2D source;
    const string Root = "Logs/CodecEncoder";
    [Serializable]
    public class Job
    {
        public string id, packet, sha256;
        public int width, height, quality;
    }
    [Serializable]
    public class Jobs
    {
        public Job[] jobs;
    }
    [Serializable]
    class Row
    {
        public string id;
        public int bytes, frames;
        public double seconds, meanFrameMs, maxFrameMs;
        public bool exact, referenceChecked;
    }
    [Serializable]
    class Results
    {
        public string gpu;
        public bool cancelledAndReused, invalidSettingsRejected;
        public Row[] rows;
    }
    readonly List<Row> rows = new List<Row>();
    Job[] jobs;
    UdonBehaviour backend, driver;
    Texture2D coefficients;
    int job, phase, frameCount, oldCap, oldVsync;
    double at, totalFrames, largestFrame;
    bool cancelled;
    void Start()
    {
        jobs = JsonUtility.FromJson<Jobs>(File.ReadAllText(Root + "/jobs.json")).jobs;
        oldCap = Application.targetFrameRate;
        oldVsync = QualitySettings.vSyncCount;
        Application.targetFrameRate = 60;
        Application.runInBackground = true;
        QualitySettings.vSyncCount = 0;
        at = Time.realtimeSinceStartupAsDouble;
    }
    void LateUpdate()
    {
        if (phase == 9) return;
        try
        {
            if (phase == 0)
            {
                foreach (var bridge in Resources
                             .FindObjectsOfTypeAll<VRC.SDK3.ClientSim.ClientSimUdonInputBehaviour>())
                    if (bridge.gameObject.scene.IsValid()) bridge.enabled = false;
                if (!Utilities.IsValid(Networking.LocalPlayer) || Time.realtimeSinceStartupAsDouble - at < 3)
                    return;
                capture.SetProgramVariable("quality", -1);
                capture.SendCustomEvent("BeginEncoding");
                if ((bool)capture.GetProgramVariable("busy") ||
                    (string)capture.GetProgramVariable("error") == "")
                    throw new Exception("Invalid quality accepted");
                capture.SetProgramVariable("quality", 60);
                BeginJob();
                return;
            }
            if (Time.realtimeSinceStartupAsDouble - at > 180) throw new Exception("Codec timeout");
            if (phase == 3)
            {
                if ((bool)backend.GetProgramVariable("readbackPending")) return;
                if ((bool)backend.GetProgramVariable("complete") ||
                    backend.GetProgramVariable("result") != null)
                    throw new Exception("Cancelled output was published");
                cancelled = true;
                driver.SendCustomEvent("BeginFixture");
                ResetTimer();
                phase = 1;
                return;
            }
            frameCount++;
            totalFrames += Time.unscaledDeltaTime * 1000;
            largestFrame = Math.Max(largestFrame, Time.unscaledDeltaTime * 1000);
            UdonBehaviour active = phase == 2 ? capture : backend;
            if ((bool)active.GetProgramVariable("busy")) return;
            if (!(bool)active.GetProgramVariable("complete"))
                throw new Exception("Encode: " + active.GetProgramVariable("error"));
            byte[] bytes = (byte[])active.GetProgramVariable(phase == 2 ? "resultAvif" : "result");
            string id = phase == 2 ? "full-capture" : jobs[job].id;
            File.WriteAllBytes(Root + "/" + id + ".avif", bytes);
            bool referenceChecked = phase != 2 || File.Exists(Root + "/capture.sha256");
            bool exact = false;
            if (referenceChecked)
            {
                string expected =
                    phase == 2 ? File.ReadAllText(Root + "/capture.sha256").Trim() : jobs[job].sha256;
                using (var hash = SHA256.Create()) exact =
                    BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() ==
                    expected;
                if (!exact) throw new Exception("AVIF bytes changed: " + id);
            }
            rows.Add(new Row { id = id, bytes = bytes.Length, frames = frameCount,
                               seconds = Time.realtimeSinceStartupAsDouble - at,
                               meanFrameMs = totalFrames / frameCount, maxFrameMs = largestFrame,
                               exact = exact, referenceChecked = referenceChecked });
            File.WriteAllText(
                Root + "/results.json",
                JsonUtility.ToJson(new Results { gpu = SystemInfo.graphicsDeviceName,
                                                 cancelledAndReused = cancelled,
                                                 invalidSettingsRejected = true, rows = rows.ToArray() },
                                   true));
            if (phase == 2)
            {
                phase = 9;
                File.WriteAllText(
                    Root + "/done.txt",
                    "PASS production GPU fixtures, cancellation/reuse, invalid quality, full capture.");
                return;
            }
            if (coefficients) Destroy(coefficients);
            if (++job < jobs.Length)
            {
                BeginJob();
                return;
            }
            if (!source) throw new Exception("Missing full-capture source image");
            Graphics.Blit(source, input);
            ResetTimer();
            phase = 2;
            capture.SendCustomEvent("BeginEncoding");
            File.WriteAllText(Root + "/progress.txt", "Full capture");
        }
        catch (Exception e)
        {
            phase = 9;
            File.WriteAllText(Root + "/error.txt", e.ToString());
            Debug.LogException(e);
        }
    }
    void BeginJob()
    {
        Job next = jobs[job];
        int index = next.width == 1920 ? 0 : next.width == 360 ? 1 : 2;
        backend = backends[index];
        driver = drivers[index];
        byte[] data = File.ReadAllBytes(next.packet);
        coefficients = new Texture2D(512, data.Length / 2048, TextureFormat.RGBA32, false, true);
        coefficients.filterMode = FilterMode.Point;
        coefficients.LoadRawTextureData(data);
        coefficients.Apply(false, false);
        driver.SetProgramVariable("coefficients", coefficients);
        driver.SetProgramVariable("quantizerIndex",
                                  Mathf.Clamp((100 - next.quality) * 255 / 100 + 16, 1, 255));
        driver.SendCustomEvent("BeginFixture");
        ResetTimer();
        phase = 1;
        if (!cancelled)
        {
            backend.SendCustomEvent("CancelEncoding");
            phase = 3;
        }
        File.WriteAllText(Root + "/progress.txt", next.id);
    }
    void ResetTimer()
    {
        at = Time.realtimeSinceStartupAsDouble;
        frameCount = 0;
        totalFrames = largestFrame = 0;
    }
    void OnDestroy()
    {
        Application.targetFrameRate = oldCap;
        QualitySettings.vSyncCount = oldVsync;
    }
}
#endif
