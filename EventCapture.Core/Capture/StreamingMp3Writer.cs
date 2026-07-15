using System.Collections.Concurrent;
using System.Diagnostics;
using EventCapture.Core.Diagnostics;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace EventCapture.Core.Capture;

public sealed class StreamingMp3Writer : IContinuousAudioSink, IDisposable
{
    private const int Mp3Bitrate = 192_000;
    private const int EncoderBufferMilliseconds = 500;
    private const int QueueCapacityChunks = 200;

    private readonly object _stateLock = new();
    private readonly BlockingPcmWaveProvider _provider;
    private readonly Task _encoderTask;
    private readonly string _temporaryPath;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private Task<string>? _completionTask;
    private bool _accepting = true;
    private bool _disposed;
    private long _chunksWritten;
    private long _bytesWritten;
    private long _lastLogTimestamp;

    private StreamingMp3Writer(string outputFolder, WaveFormat format)
    {
        Directory.CreateDirectory(outputFolder);
        OutputPath = OutputFileName.Create(outputFolder, "Audio", ".mp3");
        _temporaryPath = Path.Combine(
            outputFolder,
            $".audio-recording-{Guid.NewGuid():N}.tmp.mp3");
        _provider = new BlockingPcmWaveProvider(format, QueueCapacityChunks);
        _encoderTask = Task.Factory.StartNew(
            Encode,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            _provider.ReaderStarted.GetAwaiter().GetResult();
            TryMarkTemporaryHidden(_temporaryPath);
        }
        catch
        {
            _provider.Abort();
            TryDelete(_temporaryPath);
            DisposeProvider();
            throw;
        }

        AppLogger.Info(
            $"Streaming MP3 started | Temp={Path.GetFileName(_temporaryPath)} | " +
            $"Output={Path.GetFileName(OutputPath)} | Format={format} | " +
            $"Bitrate={Mp3Bitrate} | QueueCapacityChunks={QueueCapacityChunks}");
    }

    public string OutputPath { get; }

    public static StreamingMp3Writer Start(string outputFolder, WaveFormat format) =>
        new(outputFolder, format);

    public void WriteContinuousAudio(
        ContinuousAudioSource source,
        WaveFormat format,
        byte[] buffer,
        int count,
        long packetStartTimestamp,
        long packetDurationMilliseconds)
    {
        if (source != ContinuousAudioSource.Mixed)
            throw new InvalidOperationException("Streaming MP3 accepts only mixed audio.");
        if (!FormatsMatch(format, _provider.WaveFormat))
            throw new InvalidOperationException(
                $"Streaming MP3 format changed from {_provider.WaveFormat} to {format}.");

        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_accepting)
                throw new InvalidOperationException("Streaming MP3 is already finalizing.");
        }

        ThrowIfEncoderFailed();
        _provider.AddSamples(buffer, 0, count);
        long chunks = Interlocked.Increment(ref _chunksWritten);
        long bytes = Interlocked.Add(ref _bytesWritten, count);
        long now = Environment.TickCount64;
        long previousLog = Interlocked.Read(ref _lastLogTimestamp);
        if (chunks <= 3 || now - previousLog >= 5_000)
        {
            Interlocked.Exchange(ref _lastLogTimestamp, now);
            AppLogger.Info(
                $"Streaming MP3 status | Chunks={chunks} | Bytes={bytes} | " +
                $"QueueChunks={_provider.BufferedChunks} | QueueBytes={_provider.BufferedBytes} | " +
                $"ElapsedMs={_stopwatch.ElapsedMilliseconds}");
        }
    }

    public Task<string> CompleteAsync()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _accepting = false;
            return _completionTask ??= CompleteCoreAsync();
        }
    }

    public void Abort()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _accepting = false;
        }

        _provider.Abort();
        _ = _encoderTask.ContinueWith(
            _ =>
            {
                TryDelete(_temporaryPath);
                DisposeProvider();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        AppLogger.Info(
            $"Streaming MP3 aborted | Temp={Path.GetFileName(_temporaryPath)} | " +
            $"Chunks={Interlocked.Read(ref _chunksWritten)} | Bytes={Interlocked.Read(ref _bytesWritten)}");
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        AbortCore();
    }

    private void Encode()
    {
        try
        {
            MediaType? mediaType = MediaFoundationEncoder.SelectMediaType(
                AudioSubtypes.MFAudioFormat_MP3,
                _provider.WaveFormat,
                Mp3Bitrate);
            if (mediaType is null)
                throw new InvalidOperationException(
                    $"Windows Media Foundation has no MP3 encoder for {_provider.WaveFormat}.");

            using var encoder = new MediaFoundationEncoder(mediaType)
            {
                DefaultReadBufferSize = Math.Max(
                    _provider.WaveFormat.BlockAlign,
                    _provider.WaveFormat.AverageBytesPerSecond * EncoderBufferMilliseconds / 1000)
            };
            encoder.Encode(_temporaryPath, _provider);
        }
        catch (Exception ex)
        {
            _provider.Fail(ex);
            AppLogger.Error(nameof(StreamingMp3Writer), $"Streaming MP3 encoder failed: {ex}");
            throw;
        }
    }

    private async Task<string> CompleteCoreAsync()
    {
        var finalizeStopwatch = Stopwatch.StartNew();
        _provider.Complete();

        try
        {
            await _encoderTask.ConfigureAwait(false);
            if (!File.Exists(_temporaryPath) || new FileInfo(_temporaryPath).Length == 0)
                throw new InvalidOperationException("Streaming MP3 encoder produced an empty file.");

            File.Move(_temporaryPath, OutputPath, overwrite: false);
            ClearTemporaryAttributes(OutputPath);
            AppLogger.Info(
                $"Streaming MP3 finalized | Output={Path.GetFileName(OutputPath)} | " +
                $"FileBytes={new FileInfo(OutputPath).Length} | Chunks={Interlocked.Read(ref _chunksWritten)} | " +
                $"PcmBytes={Interlocked.Read(ref _bytesWritten)} | FinalizeMs={finalizeStopwatch.ElapsedMilliseconds} | " +
                $"TotalMs={_stopwatch.ElapsedMilliseconds}");
            return OutputPath;
        }
        catch (Exception ex)
        {
            TryDelete(_temporaryPath);
            AppLogger.Error(nameof(StreamingMp3Writer), $"Streaming MP3 finalization failed: {ex}");
            throw new InvalidOperationException("Audio recording could not be finalized.", ex);
        }
        finally
        {
            DisposeProvider();
        }
    }

    private void ThrowIfEncoderFailed()
    {
        if (!_encoderTask.IsFaulted) return;
        Exception failure = _encoderTask.Exception?.GetBaseException() ??
            new InvalidOperationException("Streaming MP3 encoder stopped unexpectedly.");
        throw new InvalidOperationException("Streaming MP3 encoder stopped unexpectedly.", failure);
    }

    private void AbortCore()
    {
        _provider.Abort();
        _ = _encoderTask.ContinueWith(
            _ =>
            {
                TryDelete(_temporaryPath);
                DisposeProvider();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeProvider()
    {
        try { _provider.Dispose(); } catch { }
    }

    private static bool FormatsMatch(WaveFormat left, WaveFormat right) =>
        left.Encoding == right.Encoding &&
        left.SampleRate == right.SampleRate &&
        left.BitsPerSample == right.BitsPerSample &&
        left.Channels == right.Channels &&
        left.BlockAlign == right.BlockAlign;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryMarkTemporaryHidden(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            File.SetAttributes(path, attributes | FileAttributes.Hidden | FileAttributes.Temporary);
        }
        catch { }
    }

    private static void ClearTemporaryAttributes(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        File.SetAttributes(
            path,
            attributes & ~(FileAttributes.Hidden | FileAttributes.Temporary));
    }

    private sealed class BlockingPcmWaveProvider : IWaveProvider, IDisposable
    {
        private readonly BlockingCollection<byte[]> _chunks;
        private readonly CancellationTokenSource _abort = new();
        private readonly TaskCompletionSource _readerStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _currentChunk;
        private int _currentOffset;
        private long _bufferedBytes;
        private bool _disposed;

        public BlockingPcmWaveProvider(WaveFormat format, int capacityChunks)
        {
            WaveFormat = format;
            _chunks = new BlockingCollection<byte[]>(
                new ConcurrentQueue<byte[]>(),
                capacityChunks);
        }

        public WaveFormat WaveFormat { get; }
        public Task ReaderStarted => _readerStarted.Task;
        public int BufferedChunks => _chunks.Count + (_currentChunk is null ? 0 : 1);
        public long BufferedBytes => Math.Max(0, Interlocked.Read(ref _bufferedBytes));

        public void AddSamples(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (count <= 0) return;
            if (count % Math.Max(1, WaveFormat.BlockAlign) != 0)
                throw new ArgumentException("PCM byte count must be block aligned.", nameof(count));

            byte[] chunk = GC.AllocateUninitializedArray<byte>(count);
            Buffer.BlockCopy(buffer, offset, chunk, 0, count);
            Interlocked.Add(ref _bufferedBytes, count);
            try
            {
                _chunks.Add(chunk, _abort.Token);
            }
            catch
            {
                Interlocked.Add(ref _bufferedBytes, -count);
                throw;
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            _readerStarted.TrySetResult();
            if (_disposed || count <= 0) return 0;

            int written = 0;
            while (written < count)
            {
                if (_currentChunk is null)
                {
                    try
                    {
                        _currentChunk = written == 0
                            ? _chunks.Take(_abort.Token)
                            : _chunks.TryTake(out byte[]? next) ? next : null;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    _currentOffset = 0;
                    if (_currentChunk is null) break;
                }

                int available = _currentChunk.Length - _currentOffset;
                int copy = Math.Min(count - written, available);
                Buffer.BlockCopy(_currentChunk, _currentOffset, buffer, offset + written, copy);
                _currentOffset += copy;
                written += copy;
                Interlocked.Add(ref _bufferedBytes, -copy);

                if (_currentOffset >= _currentChunk.Length)
                {
                    _currentChunk = null;
                    _currentOffset = 0;
                }
            }

            return written;
        }

        public void Complete()
        {
            if (!_chunks.IsAddingCompleted)
                _chunks.CompleteAdding();
        }

        public void Abort()
        {
            _readerStarted.TrySetCanceled();
            try { _abort.Cancel(); } catch { }
            try { Complete(); } catch { }
        }

        public void Fail(Exception exception)
        {
            _readerStarted.TrySetException(exception);
            try { _abort.Cancel(); } catch { }
            try { Complete(); } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Abort();
            _chunks.Dispose();
            _abort.Dispose();
        }
    }
}
