using NAudio.Wave;

namespace EventCapture.Core.Capture;

public interface IContinuousAudioSink
{
    void WriteContinuousAudio(
        WaveFormat format,
        byte[] buffer,
        int count,
        long packetStartTimestamp,
        long packetDurationMilliseconds);
}
