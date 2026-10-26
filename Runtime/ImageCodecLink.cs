using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace VrcImageCodec
{
[AddComponentMenu("Image Codec/Image Codec Link")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ImageCodecLink : UdonSharpBehaviour
{
    public ImageEncoder encoder;
    public ImageDecoder decoder;
    public RawImage display;
    [HideInInspector]
    public bool busy, complete;
    [HideInInspector]
    public string error = "", status = "Ready";
    private byte[] lastBytes;
    private bool waitingForEncoder, waitingForDecoder;

    public string ConfigurationError()
    {
        if (!encoder) return "Assign an Image Encoder.";
        if (!decoder) return "Assign an Image Decoder.";
        if (!display) return "Assign the RawImage that will display the decoded image.";
        return "";
    }

    public override void Interact()
    {
        Begin();
    }

    public void Begin()
    {
        error = ConfigurationError();
        if (error != "")
        {
            status = error;
            return;
        }
        if (busy || encoder.busy || decoder.busy || encoder.readbackPending || decoder.readbackPending)
            return;
        complete = false;
        busy = waitingForEncoder = true;
        status = "Encoding...";
        encoder.BeginEncoding();
    }

    private void Update()
    {
        if (!encoder || !decoder || !display) return;
        if (waitingForEncoder && !encoder.busy && !encoder.complete)
        {
            Finish(encoder.error == "" ? "Encoding did not complete." : encoder.error);
            return;
        }
        // Also accept encodes started by another component. Each result is forwarded once.
        if (encoder.complete && encoder.resultBytes != lastBytes && !decoder.busy && !decoder.readbackPending)
        {
            complete = false;
            busy = waitingForDecoder = true;
            waitingForEncoder = false;
            lastBytes = encoder.resultBytes;
            decoder.inputBytes = lastBytes;
            decoder.BeginDecoding();
            status = "Decoding...";
        }
        if (!waitingForDecoder || decoder.busy) return;
        waitingForDecoder = false;
        if (!decoder.complete)
        {
            Finish(decoder.error == "" ? "Decoding did not complete." : decoder.error);
            return;
        }
        display.texture = decoder.resultTexture;
        complete = true;
        Finish("");
    }

    private void Finish(string message)
    {
        busy = waitingForEncoder = false;
        error = message;
        status = error == "" ? "Complete. " + lastBytes.Length + " bytes." : error;
    }
}
}
