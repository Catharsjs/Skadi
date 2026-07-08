using NAudio.Wave;

namespace EventCapture.Core.Capture;

public enum ContinuousAudioSource
{
    System,
    Microphone,
    Mixed
}

public interface IContinuousAudioSink
{
    void WriteContinuousAudio(
        ContinuousAudioSource source,
        WaveFormat format,
        byte[] buffer,
        int count,
        long packetStartTimestamp,
        long packetDurationMilliseconds);
}