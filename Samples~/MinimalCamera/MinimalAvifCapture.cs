using UdonSharp;
using UnityEngine;
using TernaryStars;

namespace VrcAvif.Encoder.Samples
{
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MinimalAvifCapture : UdonSharpBehaviour
{
    public Camera source;
    public TernaryAvifEncoder encoder;

    public void Capture()
    {
        if (encoder.busy || encoder.gpuTransform.readbackPending)
            return;
        source.enabled = false;
        source.targetTexture = encoder.gpuTransform.inputTexture;
        source.Render();
        encoder.BeginEncoding();
    }

    private void Update()
    {
        if (encoder.busy)
            encoder.StepEncoding();
    }
}
}
