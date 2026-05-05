using LibreHardwareMonitor.Hardware;

namespace EventCapture.Core.Monitoring;

public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;

    public float CpuLoad { get; private set; }
    public float CpuFrequency { get; private set; }
    public float GpuLoad { get; private set; }
    public float GpuFrequency { get; private set; }
    public float GpuVram { get; private set; }
    public float RamUsed { get; private set; }

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true
        };
        _computer.Open();
    }

    public void Update()
    {
        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();

            foreach (var sensor in hardware.Sensors)
            {
                switch (hardware.HardwareType)
                {
                    case HardwareType.Cpu:
                        if (sensor.SensorType == SensorType.Load &&
                            sensor.Name == "CPU Total")
                            CpuLoad = sensor.Value ?? 0;
                        if (sensor.SensorType == SensorType.Clock &&
                            sensor.Name.Contains("CPU Core #1"))
                            CpuFrequency = (sensor.Value ?? 0) / 1000f;
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        if (sensor.SensorType == SensorType.Load &&
                            sensor.Name == "GPU Core")
                            GpuLoad = sensor.Value ?? 0;
                        if (sensor.SensorType == SensorType.Clock &&
                            sensor.Name == "GPU Core")
                            GpuFrequency = (sensor.Value ?? 0) / 1000f;
                        if (sensor.SensorType == SensorType.SmallData &&
                            sensor.Name == "GPU Memory Used")
                            GpuVram = (sensor.Value ?? 0) / 1024f;
                        break;

                    case HardwareType.Memory:
                        if (sensor.SensorType == SensorType.Data &&
                            sensor.Name == "Memory Used")
                            RamUsed = sensor.Value ?? 0;
                        break;
                }
            }
        }
    }

    public void Dispose()
    {
        _computer.Close();
    }
}