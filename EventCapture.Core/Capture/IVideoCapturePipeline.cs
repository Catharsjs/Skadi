namespace EventCapture.Core.Capture;

public interface IVideoCapturePipeline : IDisposable
{
    bool IsRunning { get; }
    bool IsContinuousRecording { get; }
    long StartTimestamp { get; }
    long FramesCaptured { get; }

    void Start();

    Task<(string videoPath, long videoElapsedMs, long videoStartTimestamp)>
        SaveLastSecondsAsync(string outputFolder, int seconds);

    void StartContinuousRecording(string outputFolder);

    Task<ContinuousVideoResult> StopContinuousRecordingAsync(string outputFolder);

    void Stop();
}
