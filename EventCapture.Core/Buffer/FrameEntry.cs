namespace EventCapture.Core.Buffer;

public class FrameEntry
{
    public byte[] Data { get; set; }
    public DateTime Timestamp { get; set; }

    public FrameEntry(byte[] data, DateTime timestamp)
    {
        Data = data;
        Timestamp = timestamp;
    }
}