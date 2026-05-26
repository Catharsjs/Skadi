using System.Diagnostics;
using System.Management;
using EventCapture.Core.Diagnostics;
using LibreHardwareMonitor.Hardware;
namespace EventCapture.Core.Monitoring;

// Збирає показники CPU, GPU та RAM для overlay-панелі
public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private static float _totalRamGB;

    public float CpuLoad { get; private set; }
    public float CpuFrequency { get; private set; }
    public float GpuLoad { get; private set; }
    public float GpuVram { get; private set; }
    public float RamUsed { get; private set; }
    public float TotalRamGB => _totalRamGB;
    public string CpuName { get; private set; } = string.Empty;
    public string GpuName { get; private set; } = string.Empty;
    public string RamType { get; private set; } = string.Empty;
    public int RamFrequency { get; private set; }

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsGpuEnabled = true
        };

        _computer.Open();
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue();
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
        LoadHardwareInfo();
    }

    // Статична інформація про обладнання (...
    private void LoadHardwareInfo()
    {
        LoadCpuInfo();
        LoadGpuInfo();
        LoadTotalRam();
        LoadRamInfo();
    }

    private void LoadCpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");

            foreach (var obj in searcher.Get())
            {
                string fullName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;

                int withIndex = fullName.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);

                CpuName = withIndex > 0
                        ? fullName[..withIndex]
                        : fullName;

                break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"LoadCpuInfo warning: {ex.Message}");
        }
    }

    private void LoadGpuInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'");

            foreach (var obj in searcher.Get())
            {
                GpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"LoadGpuInfo warning: {ex.Message}");
        }
    }

    private void LoadTotalRam()
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

            foreach (var obj in searcher.Get())
            {
                _totalRamGB = (float)(
                        Convert.ToUInt64(obj["TotalPhysicalMemory"]) /
                        1024.0 /
                        1024.0 /
                        1024.0);
                break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"LoadTotalRam warning: {ex.Message}");
        }
    }

    private void LoadRamInfo()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                    "SELECT SMBIOSMemoryType, Speed FROM Win32_PhysicalMemory");

            foreach (var obj in searcher.Get())
            {
                int memoryType = Convert.ToInt32(obj["SMBIOSMemoryType"]);

                RamType = memoryType switch
                {
                    26 => "DDR4",
                    34 => "DDR5",
                    _ => "DDR"
                };

                RamFrequency = Convert.ToInt32(obj["Speed"]);
                break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"LoadRamInfo warning: {ex.Message}");
        }
    }
    // ...) Статична інформація про обладнання

    // Поточні показники обладнання (...
    public void Update()
    {
        UpdateCpuLoad();
        UpdateRamUsage();
        UpdateCpuFrequency();
        UpdateGpuSensors();
    }

    private void UpdateCpuLoad()
    {
        try
        {
            CpuLoad = _cpuCounter.NextValue();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"UpdateCpuLoad warning: {ex.Message}");
        }
    }

    private void UpdateRamUsage()
    {
        try
        {
            float availableGB = _ramCounter.NextValue() / 1024f;

            RamUsed = Math.Max(0, _totalRamGB - availableGB);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"UpdateRamUsage warning: {ex.Message}");
        }
    }

    private void UpdateCpuFrequency()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                    "SELECT CurrentClockSpeed FROM Win32_Processor");

            foreach (var obj in searcher.Get())
            {
                CpuFrequency = Convert.ToInt32(obj["CurrentClockSpeed"]) / 1000f;
                break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"UpdateCpuFrequency warning: {ex.Message}");
        }
    }

    private void UpdateGpuSensors()
    {
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                hardware.Update();

                if (hardware.HardwareType != HardwareType.GpuNvidia)
                    continue;

                ReadGpuSensors(hardware);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"UpdateGpuSensors warning: {ex.Message}");
        }
    }

    private void ReadGpuSensors(IHardware hardware)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == SensorType.Load && sensor.Name == "GPU Core")
            {
                GpuLoad = sensor.Value ?? 0;
            }

            if (sensor.SensorType == SensorType.SmallData && sensor.Name == "GPU Memory Used")
            {
                GpuVram = (sensor.Value ?? 0) / 1024f;
            }
        }
    }
    // ...) Поточні показники обладнання

    // Звільнення ресурсів (...
    public void Dispose()
    {
        try
        {
            _computer.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Hardware monitor close warning: {ex.Message}");
        }

        _cpuCounter.Dispose();
        _ramCounter.Dispose();
    }
    // ...) Звільнення ресурсів
}