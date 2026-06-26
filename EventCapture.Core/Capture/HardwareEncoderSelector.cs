using System.Diagnostics;
using EventCapture.Core.Diagnostics;

namespace EventCapture.Core.Capture;

internal static class HardwareEncoderSelector
{
    private static readonly object Sync = new();
    private static string? _selectedEncoder;

    public static string GetEncoderOptions(string ffmpegPath, int fps, int bitrateKbps, int keyframeSeconds)
    {
        string encoder;
        lock (Sync)
        {
            _selectedEncoder ??= SelectEncoder(ffmpegPath);
            encoder = _selectedEncoder;
        }

        int gop = Math.Max(1, fps * keyframeSeconds);
        return encoder switch
        {
            "h264_nvenc" =>
                $"-c:v h264_nvenc -preset p4 -tune ll -rc cbr " +
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k " +
                $"-g {gop} -bf 0 -rc-lookahead 0 -surfaces 4 -delay 0 -dpb_size 1 " +
                $"-zerolatency 1 -forced-idr 1",
            "h264_qsv" =>
                $"-c:v h264_qsv -preset medium -low_power 1 -look_ahead 0 " +
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k " +
                $"-g {gop} -bf 0",
            "h264_amf" =>
                $"-c:v h264_amf -usage lowlatency -quality speed -rc cbr " +
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k " +
                $"-g {gop} -bf 0",
            "h264_mf" =>
                $"-c:v h264_mf -hw_encoding 1 -scenario display_remoting -rate_control cbr " +
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -g {gop}",
            _ =>
                $"-c:v libx264 -preset veryfast -tune zerolatency " +
                $"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k " +
                $"-g {gop} -keyint_min {gop} -sc_threshold 0 -bf 0"
        };
    }

    private static string SelectEncoder(string ffmpegPath)
    {
        // Prefer an available iGPU encoder so capture does not contend with a game
        // running on the discrete adapter. Dedicated GPU encoders remain fallbacks.
        string[] candidates = ["h264_amf", "h264_qsv", "h264_nvenc", "h264_mf", "libx264"];
        foreach (string candidate in candidates)
        {
            if (!Probe(ffmpegPath, candidate)) continue;
            AppLogger.Info($"Selected video encoder: {candidate}");
            return candidate;
        }

        throw new InvalidOperationException("No supported H.264 encoder is available.");
    }

    private static bool Probe(string ffmpegPath, string encoder)
    {
        string encoderOptions = encoder switch
        {
            "h264_nvenc" => "-c:v h264_nvenc -preset p4 -tune ll -bf 0 -surfaces 4 -zerolatency 1",
            "h264_qsv" => "-c:v h264_qsv -preset medium -low_power 1 -bf 0",
            "h264_amf" => "-c:v h264_amf -usage lowlatency -quality speed -bf 0",
            "h264_mf" => "-c:v h264_mf -hw_encoding 1 -scenario display_remoting",
            _ => "-c:v libx264 -preset veryfast -tune zerolatency"
        };

        string arguments =
            $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=30 " +
            $"-frames:v 1 -vf format=nv12 {encoderOptions} -f null NUL";

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit(5_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
