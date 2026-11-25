using System;
using System.Collections.Generic;
using System.Management;
using KochanSentinel.Core.Models;
using LibreHardwareMonitor.Hardware;

namespace KochanSentinel.Core.Services;

public sealed class SensorSnapshot
{
    public double? CpuTemp { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuLoad { get; set; }
    public double? GpuVramUsedMb { get; set; }
    public double? GpuVramTotalMb { get; set; }
    public int? FanRpm { get; set; }
    public List<SmartDriveHealth> StorageDrives { get; set; } = new();
}

public sealed class HardwareSensorService : IDisposable
{
    private Computer? _computer;
    private readonly object _lock = new();
    private bool _initialized;
    private SensorSnapshot _lastSnapshot = new();
    private DateTime _lastPoll = DateTime.MinValue;

    public void InitializeAsync()
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (_lock)
            {
                if (_initialized) return;
                try
                {
                    _computer = new Computer
                    {
                        IsCpuEnabled = true,
                        IsGpuEnabled = true,
                        IsMotherboardEnabled = true,
                        IsControllerEnabled = false,
                        IsMemoryEnabled = false,
                        IsNetworkEnabled = false,
                        IsStorageEnabled = true
                    };
                    _computer.Open();
                    _initialized = true;
                }
                catch
                {
                    _computer = null;
                }
            }
        });
    }

    public SensorSnapshot ReadSensors()
    {
        if (!_initialized || _computer == null)
            return _lastSnapshot;

        if ((DateTime.UtcNow - _lastPoll).TotalSeconds < 1.5)
            return _lastSnapshot;

        lock (_lock)
        {
            if (_computer == null) return _lastSnapshot;
            try
            {
                _lastPoll = DateTime.UtcNow;
                var snap = new SensorSnapshot();

                foreach (var hw in _computer.Hardware)
                {
                    hw.Update();
                    foreach (var sub in hw.SubHardware)
                        sub.Update();

                    if (hw.HardwareType == HardwareType.Cpu)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                 s.Name.Contains("Core Average", StringComparison.OrdinalIgnoreCase) ||
                                 snap.CpuTemp == null))
                            {
                                snap.CpuTemp = Math.Round(s.Value.Value, 1);
                            }
                        }
                    }
                    else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType == SensorType.Temperature && s.Value.HasValue &&
                                (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) || snap.GpuTemp == null))
                                snap.GpuTemp = Math.Round(s.Value.Value, 1);

                            if (s.SensorType == SensorType.Load && s.Value.HasValue &&
                                s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                                snap.GpuLoad = Math.Round(s.Value.Value, 0);

                            if (s.SensorType == SensorType.SmallData && s.Value.HasValue)
                            {
                                if (s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
                                    snap.GpuVramUsedMb = Math.Round(s.Value.Value, 0);
                                else if (s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase))
                                    snap.GpuVramTotalMb = Math.Round(s.Value.Value, 0);
                            }

                            if (s.SensorType == SensorType.Fan && s.Value.HasValue && s.Value.Value > 0 && snap.FanRpm == null)
                                snap.FanRpm = (int)Math.Round(s.Value.Value);
                        }
                    }
                    else if (hw.HardwareType == HardwareType.Motherboard && snap.FanRpm == null)
                    {
                        foreach (var sub in hw.SubHardware)
                        {
                            foreach (var s in sub.Sensors)
                            {
                                if (s.SensorType == SensorType.Fan && s.Value.HasValue && s.Value.Value > 200)
                                {
                                    snap.FanRpm = (int)Math.Round(s.Value.Value);
                                    break;
                                }
                            }
                            if (snap.FanRpm != null) break;
                        }
                    }
                    else if (hw.HardwareType == HardwareType.Storage)
                    {
                        var smart = new SmartDriveHealth { Name = hw.Name };
                        foreach (var s in hw.Sensors)
                        {
                            if (!s.Value.HasValue) continue;
                            if (s.SensorType == SensorType.Temperature && smart.TemperatureC == null)
                                smart.TemperatureC = Math.Round(s.Value.Value, 0);
                            else if (s.SensorType == SensorType.Level && s.Name.Contains("Remaining", StringComparison.OrdinalIgnoreCase))
                                smart.RemainingLifePercent = Math.Round(s.Value.Value, 0);
                            else if (s.SensorType == SensorType.Data && s.Name.Contains("Written", StringComparison.OrdinalIgnoreCase))
                                smart.TotalWrittenGb = Math.Round(s.Value.Value, 0);
                        }
                        var lifeTxt = smart.RemainingLifePercent.HasValue ? $"%{smart.RemainingLifePercent:F0} Ömür" : "Sağlıklı";
                        var tempTxt = smart.TemperatureC.HasValue ? $" • {smart.TemperatureC:F0}°C" : "";
                        var tbTxt = smart.TotalWrittenGb.HasValue ? $" • {smart.TotalWrittenGb / 1024.0:F1} TB Yazım" : "";
                        smart.SummaryText = $"{lifeTxt}{tempTxt}{tbTxt}";
                        snap.StorageDrives.Add(smart);
                    }
                }

                _lastSnapshot = snap;
                return snap;
            }
            catch
            {
                return _lastSnapshot;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _computer?.Close(); }
            catch { }
            _computer = null;
            _initialized = false;
        }
    }
}
