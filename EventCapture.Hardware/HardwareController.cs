using System.IO.Ports;
namespace EventCapture.Hardware;

public sealed class HardwareController : IDisposable
{
    private SerialPort? _serialPort;
    private Thread? _readThread;
    private volatile bool _isRunning;
    private DateTime _lastHandshakeTime = DateTime.MinValue;
    private DateTime _lastActionTime = DateTime.MinValue;
    public event Action<HardwareCommand>? CommandReceived;
    public event Action<string>? LogReceived;

    public bool IsConnected => _serialPort?.IsOpen == true;

    // ── Підключення до конкретного порту ──────────────────────
    public void Start(string portName, int baudRate = 115200)
    {
        Stop();

        _serialPort = new SerialPort(portName, baudRate)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            DtrEnable = false,
            RtsEnable = false
        };

        _serialPort.Open();
        Thread.Sleep(300);             // дати буферу заповнитись
        _serialPort.DiscardInBuffer(); // і скинути все одразу

        _isRunning = true;

        _readThread = new Thread(ReadLoop) { IsBackground = true };
        _readThread.Start();

        LogReceived?.Invoke($"Hardware connected: {portName}");
    }

    // ── Автоматичний пошук ESP32 на всіх доступних портах ─────
    // ESP32 надсилає EVENTCAPTURE_DEVICE кожні 10 сек,
    // тому чекаємо до timeoutMs мілісекунд.
    public static async Task<string?> AutoDetectAsync(int timeoutMs = 15000)
    {
        var ports = GetAvailablePorts();
        if (ports.Length == 0) return null;

        var cts = new CancellationTokenSource(timeoutMs);
        var tcs = new TaskCompletionSource<string?>();

        foreach (var port in ports)
        {
            var p = port;
            _ = Task.Run(() =>
            {
                SerialPort? sp = null;
                try
                {
                    sp = new SerialPort(p, 115200)
                    {
                        NewLine = "\n",
                        ReadTimeout = 500,
                        DtrEnable = false,
                        RtsEnable = false
                    };

                    sp.Open();

                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            var line = sp.ReadLine().Trim();
                            if (line.Equals("EVENTCAPTURE_DEVICE",
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                sp.Close();
                                sp.Dispose();
                                sp = null;
                                tcs.TrySetResult(p);
                                return;
                            }
                        }
                        catch (TimeoutException) { }
                    }
                }
                catch { }
                finally
                {
                    try { sp?.Close(); sp?.Dispose(); } catch { }
                }
            });
        }

        cts.Token.Register(() => tcs.TrySetResult(null));
        return await tcs.Task;
    }

    // ── Зупинка ────────────────────────────────────────────────
    public void Stop()
    {
        _isRunning = false;

        try { _readThread?.Join(1000); }
        catch { }
        _readThread = null;

        try
        {
            if (_serialPort?.IsOpen == true)
                _serialPort.Close();
        }
        catch { }

        _serialPort?.Dispose();
        _serialPort = null;
    }

    // ── Цикл читання ──────────────────────────────────────────
    private void ReadLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                    return;

                string line = _serialPort.ReadLine().Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var cmd = ParseCommand(line);

                // Дебаунс хендшейку — не частіше ніж раз на 30 сек
                if (cmd == HardwareCommand.DeviceHandshake)
                {
                    if ((DateTime.Now - _lastHandshakeTime).TotalSeconds < 30)
                        continue;
                    _lastHandshakeTime = DateTime.Now;
                }
                if (cmd == HardwareCommand.Screenshot || cmd == HardwareCommand.SaveVideo)
                {
                    if ((DateTime.Now - _lastActionTime).TotalSeconds < 1)
                        continue;
                    _lastActionTime = DateTime.Now;
                }
                LogReceived?.Invoke($"Hardware command: {line}");
                CommandReceived?.Invoke(cmd);
            }
            catch (TimeoutException) { }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"Hardware error: {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    // ── Парсинг команди ───────────────────────────────────────
    private static HardwareCommand ParseCommand(string line)
    {
        return line.ToUpperInvariant() switch
        {
            "SCREENSHOT" => HardwareCommand.Screenshot,
            "SAVE_VIDEO" => HardwareCommand.SaveVideo,
            "PING" => HardwareCommand.Ping,
            "READY" => HardwareCommand.Ready,
            "EVENTCAPTURE_DEVICE" => HardwareCommand.DeviceHandshake,
            _ => HardwareCommand.Unknown
        };
    }

    // ── Список доступних портів ───────────────────────────────
    public static string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames()
            .OrderBy(x => x)
            .ToArray();
    }

    public void Dispose() => Stop();
}