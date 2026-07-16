using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

internal enum NativeAudioStreamKind { System = 0, Microphone = 1 }
internal enum NativeResult { Ok, InvalidArgument, InvalidState, NotSupported, Timeout, NativeFailure }
internal enum NativeTargetKind { Monitor }

[StructLayout(LayoutKind.Sequential)]
internal struct NativeAudioStreamConfig
{
    public uint SampleRate;
    public uint Channels;
    public uint BitsPerSample;
    public int Enabled;

    public static NativeAudioStreamConfig From(WaveFormat? format)
    {
        if (format is null) return default;
        int bits = format.Encoding == WaveFormatEncoding.IeeeFloat ? 16 : format.BitsPerSample;
        if (format.SampleRate <= 0 || format.Channels <= 0 || bits <= 0) return default;
        return new NativeAudioStreamConfig
        {
            SampleRate = (uint)format.SampleRate,
            Channels = (uint)format.Channels,
            BitsPerSample = (uint)bits,
            Enabled = 1
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoConfig
{
    public uint StructSize;
    public NativeTargetKind TargetKind;
    public IntPtr TargetHandle;
    public uint OutputWidth;
    public uint OutputHeight;
    public uint FramesPerSecond;
    public uint BitrateKbps;
    public uint ReplaySeconds;
    public int EnableReplay;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeExportResult
{
    public uint StructSize;
    public long StartTimestamp100ns;
    public long EndTimestamp100ns;
    public ulong FrameCount;
    public static NativeExportResult Create() => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeExportResult>()
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeVideoStats
{
    public uint StructSize;
    public ulong CapturedFrames;
    public ulong EncodedFrames;
    public ulong DroppedFrames;
    public ulong BufferedBytes;
    public ulong BufferedFrames;
    public int IsRunning;
    public int IsRecording;
    public static NativeVideoStats Create() => new()
    {
        StructSize = (uint)Marshal.SizeOf<NativeVideoStats>()
    };
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint(int x, int y)
{
    public readonly int X = x;
    public readonly int Y = y;
}

internal sealed class SafeVideoEngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeVideoEngineHandle() : base(true) { }

    protected override bool ReleaseHandle()
    {
        NativeMethods.DestroyVideoEngine(handle);
        return true;
    }
}

internal static class NativeMethods
{
    private const string Library = "EventCapture.Native.dll";

    [DllImport(Library, EntryPoint = "EcCreateVideoEngine")]
    internal static extern NativeResult CreateVideoEngine(in NativeVideoConfig config, out SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcStartVideoEngine")]
    internal static extern NativeResult StartVideoEngine(SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcSaveReplay", CharSet = CharSet.Unicode)]
    internal static extern NativeResult SaveReplay(SafeVideoEngineHandle handle, string path, uint seconds, ref NativeExportResult result);
    [DllImport(Library, EntryPoint = "EcMuxReplayAudio", CharSet = CharSet.Unicode)]
    internal static extern NativeResult MuxReplayAudio(string videoPath, string audioPath, string outputPath);
    [DllImport(Library, EntryPoint = "EcStartRecording", CharSet = CharSet.Unicode)]
    internal static extern NativeResult StartRecording(SafeVideoEngineHandle handle, string path);
    [DllImport(Library, EntryPoint = "EcStartRecordingWithAudio", CharSet = CharSet.Unicode)]
    internal static extern NativeResult StartRecordingWithAudio(SafeVideoEngineHandle handle, string path, ref NativeAudioStreamConfig systemAudio, ref NativeAudioStreamConfig microphoneAudio);
    [DllImport(Library, EntryPoint = "EcWriteRecordingAudio")]
    internal static extern NativeResult WriteRecordingAudio(SafeVideoEngineHandle handle, NativeAudioStreamKind streamKind, byte[] data, uint byteCount, long timestamp100ns, long duration100ns);
    [DllImport(Library, EntryPoint = "EcStopRecording")]
    internal static extern NativeResult StopRecording(SafeVideoEngineHandle handle, ref NativeExportResult result);
    [DllImport(Library, EntryPoint = "EcGetVideoStats")]
    internal static extern NativeResult GetVideoStats(SafeVideoEngineHandle handle, ref NativeVideoStats stats);
    [DllImport(Library, EntryPoint = "EcStopVideoEngine")]
    internal static extern NativeResult StopVideoEngine(SafeVideoEngineHandle handle);
    [DllImport(Library, EntryPoint = "EcDestroyVideoEngine")]
    internal static extern void DestroyVideoEngine(IntPtr handle);
    [DllImport(Library, EntryPoint = "EcGetLastError", CharSet = CharSet.Unicode)]
    internal static extern uint GetLastError(SafeVideoEngineHandle handle, StringBuilder? buffer, uint capacity);
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
}
