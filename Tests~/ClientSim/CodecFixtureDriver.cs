using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VrcImageCodec;

// Fixture injection is test-only; encoding still executes in the production GPU backend.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CodecFixtureDriver : UdonSharpBehaviour
{
    public AvifGpuEntropy backend;
    public Texture2D coefficients;
    public RenderTexture packet;
    public int quantizerIndex;
    public void BeginFixture()
    {
        VRCGraphics.Blit(coefficients, packet);
        backend.BeginEncoding(packet, quantizerIndex);
    }
}
