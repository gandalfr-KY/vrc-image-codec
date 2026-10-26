using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace VrcImageCodec.Samples
{
[AddComponentMenu("Image Codec/Samples/Image Codec Link")]
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ImageCodecLink : UdonSharpBehaviour
{
    public ImageEncoder encoder;
    public ImageDecoder decoder;
    public RawImage display;
    private bool busy, complete;
    private string error = "", status = "Ready";
    public bool IsBusy => busy;
    public bool IsComplete => complete;
    public string LastError => error;
    public string Status => status;
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
        if (busy || encoder.IsBusy || decoder.IsBusy || encoder.HasPendingReadback ||
            decoder.HasPendingReadback)
            return;
        complete = false;
        busy = waitingForEncoder = true;
        status = "Encoding...";
        encoder.BeginEncoding();
    }

    private void Update()
    {
        if (!encoder || !decoder || !display) return;
        if (waitingForEncoder && !encoder.IsBusy && !encoder.IsComplete)
        {
            Finish(encoder.LastError == "" ? "Encoding did not complete." : encoder.LastError);
            return;
        }
        // Also accept encodes started by another component. Each result is forwarded once.
        if (encoder.IsComplete && encoder.ResultBytes != lastBytes && !decoder.IsBusy &&
            !decoder.HasPendingReadback)
        {
            complete = false;
            busy = waitingForDecoder = true;
            waitingForEncoder = false;
            lastBytes = encoder.ResultBytes;
            decoder.TryBeginDecoding(lastBytes);
            status = "Decoding...";
        }
        if (!waitingForDecoder || decoder.IsBusy) return;
        waitingForDecoder = false;
        if (!decoder.IsComplete)
        {
            Finish(decoder.LastError == "" ? "Decoding did not complete." : decoder.LastError);
            return;
        }
        display.texture = decoder.ResultTexture;
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
