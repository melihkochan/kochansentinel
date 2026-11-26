using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private bool _driverlessMode;
    private SensorSnapshot _lastSnapshot = new();
    private DateTime _lastPoll = DateTime.MinValue;
    private bool _nvidiaSmiAvailable = true;

    public string DriverStatus { get; private set; } = "Başlatılıyor...";

    public void InitializeAsync(bool safeDriverlessMode = false)
    {
        _driverlessMode = safeDriverlessMode;
        if (safeDriverlessMode)
        {
            DisposeComputerOnly();
            DriverStatus = "Sürücüsüz Güvenli Mod (WinRing0 Kapalı — Defender Dostu)";
            _initialized = true;
            return;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            lock (_lock)
            {
                if (_initialized && _computer != null) return;
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
                    DriverStatus = "LibreHardwareMonitor Aktif";
                }
                catch
                {
                    _computer = null;
                    _driverlessMode = true;
                    _initialized = true;
                    DriverStatus = "Güvenli Fallback Modu (Sensör sürücüsü engellendi)";
                }
            }
        });
    }

    public void SetDriverlessMode(bool safeDriverlessMode)
    {
        if (_driverlessMode == safeDriverlessMode && _initialized) return;
        InitializeAsync(safeDriverlessMode);
    }

    public SensorSnapshot ReadSensors()
    {
        if (!_initialized)
            return _lastSnapshot;

        if ((DateTime.UtcNow - _lastPoll).TotalSeconds < 1.5)
            return _lastSnapshot;

        if (_driverlessMode || _computer == null)
        {
            _lastPoll = DateTime.UtcNow;
            var fallback = ReadDriverlessFallback();
            _lastSnapshot = fallback;
            return fallback;
        }

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

    private SensorSnapshot ReadDriverlessFallback()
    {
        var snap = new SensorSnapshot();
        // Zero ring-0 driver needed: read NVIDIA GPU telemetry cleanly via nvidia-smi if present
        if (_nvidiaSmiAvailable)
        {
            try
            {
                var psi = new ProcessStartInfo("nvidia-smi",
                    "--query-gpu=temperature.gpu,utilization.gpu,memory.used,memory.total,fan.speed --format=csv,noheader,nounits")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var line = p.StandardOutput.ReadLine();
                    p.WaitForExit(1200);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        var parts = line.Split(',', StringSplitOptions.TrimEntries);
                        if (parts.Length >= 4)
                        {
                            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var gt))
                                snap.GpuTemp = Math.Round(gt, 1);
                            if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var gl))
                                snap.GpuLoad = Math.Round(gl, 0);
                            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vu))
                                snap.GpuVramUsedMb = Math.Round(vu, 0);
                            if (double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vt))
                                snap.GpuVramTotalMb = Math.Round(vt, 0);
                        }
                    }
                }
            }
            catch
            {
                _nvidiaSmiAvailable = false;
            }
        }
        return snap;
    }

    private void DisposeComputerOnly()
    {
        lock (_lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
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
