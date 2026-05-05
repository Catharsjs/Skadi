using EventCapture.Core.Buffer;
using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Extensions.System.Drawing.Common;
using FFMpegCore.Pipes;
using System.Drawing;
namespace EventCapture.Core.Capture;

public class VideoSaver
{
    private readonly string _outputFolder;

    public VideoSaver(string outputFolder)
    {
        _outputFolder = outputFolder;
        Directory.CreateDirectory(outputFolder);
    }

    public async Task<string> SaveVideoAsync(RingBuffer<FrameEntry> buffer, int fps = 15)
    {
        var frames = buffer.ReadAll();
        if (frames.Length == 0)
            throw new InvalidOperationException("Buffer is empty.");

        string fileName = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4";
        string filePath = Path.Combine(_outputFolder, fileName);

        var videoFrames = frames.Select(f => new BitmapVideoFrameWrapper(BytesToBitmap(f.Data)));
        var source = new RawVideoPipeSource(videoFrames) { FrameRate = fps };

        await FFMpegArguments
            .FromPipeInput(source)
            .OutputToFile(filePath, overwrite: true, options => options
                .WithVideoCodec("h264_mf")
                .WithVideoBitrate(8000))
            .ProcessAsynchronously();

        foreach (var frame in videoFrames)
            frame.Dispose();

        return filePath;
    }

    private static Bitmap BytesToBitmap(byte[] data)
    {
        var ms = new MemoryStream(data);
        return new Bitmap(ms);
    }
}