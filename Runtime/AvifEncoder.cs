using UdonSharp;
using UnityEngine;

namespace VrcImageCodec
{
// Coordinates GPU stages. Image coding and AVIF assembly stay on the GPU.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class AvifEncoder : UdonSharpBehaviour
{
    public const int DefaultQuality = 60;
    public bool subsampleChroma = true;
    public int width = 640, height = 360;
    [Range(0, 100)]
    public int quality = DefaultQuality;
    public AvifGpuTransform gpuTransform;
    public AvifGpuEntropy gpuEntropy;
    [HideInInspector]
    public byte[] resultAvif;
    [HideInInspector]
    public bool busy, complete;
    [HideInInspector]
    public string error = "";
    [HideInInspector]
    public float progress, elapsedSeconds, cpuSeconds, maxStepMs, lastWorkTime;
    private float startedAt;
    private int stage, quantizerIndex;
    private int[] dcQuant = AvifTables.CreateDcQuant();
    private int[] acQuant = AvifTables.CreateAcQuant();

    public void BeginEncoding()
    {
        if (gpuEntropy && gpuEntropy.readbackPending)
        {
            error = "Previous AVIF GPU readback is still pending.";
            return;
        }
        CancelEncoding();
        if (!gpuTransform || !gpuEntropy || !gpuTransform.material || !gpuEntropy.syntaxMaterial ||
            !gpuEntropy.cdfMaterial || !gpuEntropy.rangeMaterial || !gpuEntropy.initialCdf ||
            !gpuEntropy.containerHeaders || width != gpuEntropy.width || height != gpuEntropy.height ||
            subsampleChroma != gpuEntropy.subsampleChroma || quality < 0 || quality > 100)
        {
            error = "Configure the encoder's GPU resources and dimensions before encoding.";
            return;
        }
        quantizerIndex = Mathf.Clamp((100 - quality) * 255 / 100 + 16, 1, 255);
        gpuTransform.subsampleChroma = subsampleChroma;
        gpuTransform.width = width;
        gpuTransform.height = height;
        gpuTransform.quantDc = dcQuant[quantizerIndex];
        gpuTransform.quantAc = acQuant[quantizerIndex];
        gpuTransform.material.SetInt("_QuantUvDc", dcQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        gpuTransform.material.SetInt("_QuantUvAc", acQuant[Mathf.Clamp(quantizerIndex - 16, 0, 255)]);
        startedAt = lastWorkTime = Time.realtimeSinceStartup;
        elapsedSeconds = cpuSeconds = maxStepMs = 0;
        gpuTransform.BeginTransform();
        if (!gpuTransform.busy)
        {
            error = gpuTransform.error;
            return;
        }
        busy = true;
        stage = 0;
    }

    public void CancelEncoding()
    {
        if (gpuTransform) gpuTransform.CancelTransform();
        if (gpuEntropy) gpuEntropy.CancelEncoding();
        busy = complete = false;
        error = "";
        resultAvif = null;
        progress = 0;
    }

    private void Update()
    {
        if (busy) StepEncoding();
    }

    public void StepEncoding()
    {
        if (!busy) return;
        lastWorkTime = Time.realtimeSinceStartup;
        elapsedSeconds = lastWorkTime - startedAt;
        if (stage == 0 && gpuTransform.busy) return;
        if (stage == 1 && gpuEntropy.busy) return;
        float before = Time.realtimeSinceStartup;
        if (stage == 0)
        {
            if (!gpuTransform.complete)
            {
                Fail(gpuTransform.error);
                return;
            }
            gpuEntropy.BeginEncoding(gpuTransform.packedTexture, quantizerIndex);
            stage = 1;
            progress = .8f;
            float work = Time.realtimeSinceStartup - before;
            cpuSeconds += work;
            maxStepMs = Mathf.Max(maxStepMs, work * 1000);
            return;
        }
        if (!gpuEntropy.complete)
        {
            Fail(gpuEntropy.error);
            return;
        }
        resultAvif = gpuEntropy.result;
        gpuEntropy.result = null;
        busy = false;
        complete = true;
        progress = 1;
    }

    private void Fail(string message)
    {
        busy = complete = false;
        error = message;
    }
}
}
