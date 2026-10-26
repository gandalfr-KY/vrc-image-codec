using UdonSharp;
using UnityEngine;
using VrcImageCodec;

namespace VrcImageCodec.Samples
{
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MinimalAvifCapture : UdonSharpBehaviour
{
    public Camera source;
    public ImageEncoder encoder;

    public void Capture()
    {
        if (encoder.IsBusy || encoder.HasPendingReadback) return;
        source.enabled = false;
        source.targetTexture = encoder.InputTexture;
        source.Render();
        encoder.BeginEncoding();
    }
}
}
