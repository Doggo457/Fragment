namespace Fragment.Services.Encoding;

/// <summary>One encoded H.264 access unit copied out of the encoder, held in the replay ring.</summary>
public sealed class EncodedVideoSample
{
    public required byte[] Data;   // contiguous compressed bitstream (encoder's output format)
    public long TimeNs;            // presentation time, 100-ns units, on the shared capture clock
    public long DurNs;
    public bool KeyFrame;          // MFSampleExtension_CleanPoint — a clip must start here
}

/// <summary>One chunk of interleaved 16-bit PCM audio held in the replay ring (encoded to AAC only at save).</summary>
public sealed class AudioPcmChunk
{
    public required byte[] Pcm;    // interleaved 16-bit PCM (a copy; the capture buffer is reused)
    public int Count;             // valid bytes in Pcm
    public long TimeNs;            // start time, 100-ns units, same clock as video
    public long DurNs;
}
