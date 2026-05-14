using LibreHardwareMonitor.Hardware;
using System.Diagnostics;
using System.Management;

namespace EventCapture.Core.Monitoring;

// Збирає дані про CPU, GPU та RAM
// CPU/RAM — через PerformanceCounter (не потребує прав адміна)
// GPU — через LibreHardwareMonitor (тільки Nvidia)
// Назви та характеристики — через WMI (завантажуються один раз при старті)
public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly PerformanceCounter _cpuCounter;
    private readonly PerformanceCounter _ramCounter;
    private static float _totalRamGB = 0;

    // ─── Поточні показники (оновлюються через Update) ────────────────────
    public float CpuLoad { get; private set; }
    public float CpuFrequency { get; private set; }
    public float GpuLoad { get; private set; }
    public float GpuVram { get; private set; }
    public float RamUsed { get; private set; }
    public float TotalRamGB => _totalRamGB;

    // ─── Статична інформація (завантажується один раз) ───────────────────
    public string CpuName { get; private set; } = string.Empty;
    public string GpuName { get; private set; } = string.Empty;
    public string RamType { get; private set; } = string.Empty;
    public int RamFrequency { get; private set; }

    public HardwareMonitor()
    {
        _computer = new Computer { IsGpuEnabled = true };
        _computer.Open();

        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _cpuCounter.NextValue(); // перший виклик завжди повертає 0 — ігноруємо

        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");

        LoadHardwareInfo();
    }

    // Завантажуємо назви і характеристики через WMI один раз при старті
    private void LoadHardwareInfo()
    {
        try
        {
            using var cpuSearch = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var obj in cpuSearch.Get())
            {
                var fullName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                // Обрізаємо суфікс " with Radeon Graphics" для AMD
                int withIndex = fullName.IndexOf(" with ", StringComparison.OrdinalIgnoreCase);
                CpuName = withIndex > 0 ? fullName[..withIndex] : fullName;
            }
        }
        catch { }

        try
        {
            using var gpuSearch = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'");
            foreach (var obj in gpuSearch.Get())
            {
                GpuName = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                break;
            }
        }
        catch { }

        try
        {
            using var ramSearch = new ManagementObjectSearcher(
                "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (var obj in ramSearch.Get())
                _totalRamGB = (float)(Convert.ToUInt64(obj["TotalPhysicalMemory"]) / 1024.0 / 1024.0 / 1024.0);
        }
        catch { }

        try
        {
            using var ramTypeSearch = new ManagementObjectSearcher(
                "SELECT SMBIOSMemoryType, Speed FROM Win32_PhysicalMemory");
            foreach (var obj in ramTypeSearch.Get())
            {
                int memType = Convert.ToInt32(obj["SMBIOSMemoryType"]);
                RamType = memType switch
                {
                    26 => "DDR4",
                    34 => "DDR5",
                    _ => "DDR"
                };
                RamFrequency = Convert.ToInt32(obj["Speed"]);
                break;
            }
        }
        catch { }
    }

    // Викликається раз на секунду з MainForm.StartHardwareMonitor
    public void Update()
    {
        CpuLoad = _cpuCounter.NextValue();

        float availableGB = _ramCounter.NextValue() / 1024f;
        RamUsed = _totalRamGB - availableGB;

        // Поточна частота CPU через WMI (оновлюється ~раз на секунду)
        try
        {
            using var cpuFreqSearch = new ManagementObjectSearcher(
                "SELECT CurrentClockSpeed FROM Win32_Processor");
            foreach (var obj in cpuFreqSearch.Get())
            {
                CpuFrequency = Convert.ToInt32(obj["CurrentClockSpeed"]) / 1000f;
                break;
            }
        }
        catch { }

        // GPU дані через LibreHardwareMonitor (тільки Nvidia)
        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();

            if (hardware.HardwareType == HardwareType.GpuNvidia)
            {
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Load &&
                        sensor.Name == "GPU Core")
                        GpuLoad = sensor.Value ?? 0;

                    if (sensor.SensorType == SensorType.SmallData &&
                        sensor.Name == "GPU Memory Used")
                        GpuVram = (sensor.Value ?? 0) / 1024f;
                }
            }
        }
    }

    public void Dispose()
    {
        _computer.Close();
        _cpuCounter.Dispose();
        _ramCounter.Dispose();
    }
}