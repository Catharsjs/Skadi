using EventCapture.Core.Buffer;
using FFMpegCore;
using FFMpegCore.Extensions.System.Drawing.Common;
using FFMpegCore.Pipes;
using System.Drawing;
using System.Drawing.Imaging;

namespace EventCapture.Core.Capture;

public class VideoSaver
{
    private readonly string _outputFolder;

    public VideoSaver(string outputFolder)
    {
        _outputFolder = outputFolder;
        Directory.CreateDirectory(outputFolder);
    }

    public async Task<string> SaveVideoAsync(RingBuffer<FrameEntry> buffer, int fps, int maxSeconds)
    {
        var frames = buffer.ReadAll();
        if (frames.Length == 0)
            throw new InvalidOperationException("Buffer is empty.");

        // Беремо тільки останні maxSeconds секунд
        int maxFrames = fps * maxSeconds;
        if (frames.Length > maxFrames)
            frames = frames[^maxFrames..];

        string fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4";
        string filePath = Path.Combine(_outputFolder, fileName);

        var source = new RawVideoPipeSource(StreamFrames(frames)) { FrameRate = fps };

        await FFMpegArguments
            .FromPipeInput(source)
            .OutputToFile(filePath, overwrite: true, options => options
                .WithVideoCodec("h264_mf")
                .WithVideoBitrate(8000)
                .WithFramerate(fps))
            .ProcessAsynchronously();

        return filePath;
    }

    private static IEnumerable<IVideoFrame> StreamFrames(FrameEntry[] frames)
    {
        foreach (var frame in frames)
        {
            Bitmap? bmp = null;
            BitmapVideoFrameWrapper? wrapper = null;
            try
            {
                bmp = BytesToBitmap(frame.Data);
                wrapper = new BitmapVideoFrameWrapper(bmp);
                yield return wrapper;
            }
            finally
            {
                wrapper?.Dispose();
                bmp?.Dispose();
            }
        }
    }

    private static Bitmap BytesToBitmap(byte[] data)
    {
        var ms = new MemoryStream(data);
        return new Bitmap(ms);
    }
}