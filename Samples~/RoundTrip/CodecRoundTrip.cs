using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace VrcImageCodec.Samples
{
// Only sample image initialization and a status label; transfer lives in ImageCodecLink.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class CodecRoundTrip : UdonSharpBehaviour
{
    public ImageCodecLink link;
    public Texture sourceImage;
    public RawImage original;
    public Text status;

    private void Start()
    {
        if (sourceImage) VRCGraphics.Blit(sourceImage, link.encoder.inputTexture);
        original.texture = link.encoder.inputTexture;
    }

    private void Update()
    {
        status.text = link.status;
    }
}
}
