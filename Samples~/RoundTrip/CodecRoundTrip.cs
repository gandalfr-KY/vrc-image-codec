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
        if (sourceImage) VRCGraphics.Blit(sourceImage, link.encoder.InputTexture);
        original.texture = link.encoder.InputTexture;
    }

    private void Update()
    {
        status.text = link.Status;
    }
}
}
