using UdonSharp;
using UnityEngine;
using VrcImageCodec;

namespace VrcAvif.Encoder.Samples
{
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MinimalAvifCapture : UdonSharpBehaviour
{
    public Camera source;
    public AvifEncoder encoder;

    public void Capture()
    {
        if (encoder.busy || encoder.gpuEntropy.readbackPending)
            return;
        source.enabled = false;
        source.targetTexture = encoder.gpuTransform.inputTexture;
        source.Render();
        encoder.BeginEncoding();
    }
}
}
