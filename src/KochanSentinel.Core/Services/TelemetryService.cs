using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class TelemetryService : IDisposable
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private PerformanceCounter? _diskTimeCounter;
    private readonly HardwareSensorService _sensorService = new();
    private readonly FpsMonitorService _fpsMonitor = new();
    private readonly ProcessNetworkMonitorService _procNetMonitor = new();
    private List<(int Pid, string Name)> _lastProcessNames = new();
    private Dictionary<int, (double DownKb, double UpKb)> _lastNetRates = new();

    public IReadOnlyList<(int Pid, string Name)> LastProcessNamesSnapshot => _lastProcessNames;

    private string _cpuName = "İşlemci";
    private string _gpuName = "Ekran Kartı";
    private long _lastNetBytesReceived;
    private long _lastNetBytesSent;
    private DateTime _lastNetSample = DateTime.UtcNow;

    private readonly Dictionary<int, (TimeSpan CpuTime, DateTime SampleTime)> _prevProcCpu = new();
    private readonly Dictionary<int, (ulong IoBytes, DateTime SampleTime)> _prevProcIo = new();
    private readonly ConcurrentDictionary<string, bool> _signatureCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CriticalSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "services", "lsass", "svchost",
        "winlogon", "dwm", "explorer", "Registry", "Memory Compression", "MsMpEng"
    };

    public bool IsElevated { get; }

    public TelemetryService()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);

        // Initialize PerformanceCounters, WMI & LibreHardwareMonitor asynchronously
        _sensorService.InitializeAsync(safeDriverlessMode: false);
        if (IsElevated)
        {
            _fpsMonitor.Start();
            _procNetMonitor.Start();
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            InitCounters();
            LoadHardwareNames();
        });
    }

    public void SetSafeDriverlessSensors(bool safeDriverlessMode)
    {
        _sensorService.SetDriverlessMode(safeDriverlessMode);
    }

    public SensorSnapshot ReadSensorsNow() => _sensorService.ReadSensors();

    private void InitCounters()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            _cpuCounter.NextValue();

            _diskTimeCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
            _diskTimeCounter.NextValue();

            _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            _diskReadCounter.NextValue();

            _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            _diskWriteCounter.NextValue();
        }
        catch
        {
            // Performance counters might be disabled on some stripped Windows builds
        }
    }

    private void LoadHardwareNames()
    {
        try
        {
            using var cpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            foreach (var obj in cpuSearcher.Get())
            {
                _cpuName = obj["Name"]?.ToString()?.Trim() ?? _cpuName;
                break;
            }
        }
        catch { }

        try
        {
            using var gpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            var gpus = new List<string>();
            foreach (var obj in gpuSearcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(name)) gpus.Add(name);
            }
            if (gpus.Count > 0)
                _gpuName = string.Join(" / ", gpus);
        }
        catch { }
    }

    private DateTime _lastPingSample = DateTime.MinValue;
    private int? _cachedPingMs;
    private double _cachedJitterMs;
    private double _cachedPacketLossPct;
    private readonly Queue<int?> _pingWindow = new();
    private bool _pingInFlight;

    public SystemMetrics CaptureMetrics(int highRiskFindings = 0, int mediumRiskFindings = 0)
    {
        var metrics = new SystemMetrics
        {
            CpuName = _cpuName,
            GpuName = _gpuName,
            CpuLogicalProcessors = Environment.ProcessorCount,
            CpuCores = Math.Max(1, Environment.ProcessorCount / 2),
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            IsElevated = IsElevated,
            ActivePowerPlan = GetActivePowerPlanName(),
            PingMs = _cachedPingMs,
            PingJitterMs = _cachedJitterMs,
            PacketLossPercent = _cachedPacketLossPct
        };

        SamplePingAsync();

        try
        {
            if (_cpuCounter != null)
                metrics.CpuUsagePercent = Math.Round(Math.Clamp(_cpuCounter.NextValue(), 0, 100), 1);

            if (_diskTimeCounter != null)
                metrics.DiskUsagePercent = Math.Round(Math.Clamp(_diskTimeCounter.NextValue(), 0, 100), 1);

            if (_diskReadCounter != null)
                metrics.DiskReadMbps = Math.Round(_diskReadCounter.NextValue() / (1024.0 * 1024.0), 2);

            if (_diskWriteCounter != null)
                metrics.DiskWriteMbps = Math.Round(_diskWriteCounter.NextValue() / (1024.0 * 1024.0), 2);
        }
        catch { }

        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            metrics.RamTotalGb = Math.Round(mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 2);
            metrics.RamAvailableGb = Math.Round(mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0), 2);
            metrics.RamUsedGb = Math.Round(metrics.RamTotalGb - metrics.RamAvailableGb, 2);
            metrics.RamUsagePercent = mem.dwMemoryLoad;
        }

        SampleNetwork(metrics);
        SampleDrives(metrics);

        // Properly dispose all Process handles while capturing lightweight snapshot for GameModeService
        var rawProcs = Process.GetProcesses();
        var nameSnapshot = new List<(int Pid, string Name)>(rawProcs.Length);
        foreach (var rp in rawProcs)
        {
            try { nameSnapshot.Add((rp.Id, rp.ProcessName)); }
            catch { }
            finally { rp.Dispose(); }
        }
        _lastProcessNames = nameSnapshot;
        metrics.ActiveProcessCount = rawProcs.Length;

        if (IsElevated)
        {
            var snap = _sensorService.ReadSensors();
            metrics.CpuTempCelsius = snap.CpuTemp ?? TryReadCpuTemperature();
            metrics.GpuTempCelsius = snap.GpuTemp;
            metrics.GpuLoadPercent = snap.GpuLoad;
            metrics.GpuVramUsedMb = snap.GpuVramUsedMb;
            metrics.GpuVramTotalMb = snap.GpuVramTotalMb;
            metrics.FanRpm = snap.FanRpm;
            metrics.CurrentFps = _fpsMonitor.GetForegroundFps();
            metrics.SmartDrives = snap.StorageDrives;
            metrics.SensorDriverStatus = _sensorService.DriverStatus;
        }
        else
        {
            metrics.SensorDriverStatus = "Standart Mod";
        }

        ComputeHealthScore(metrics, highRiskFindings, mediumRiskFindings);
        return metrics;
    }

    private void SamplePingAsync()
    {
        if (_pingInFlight || (DateTime.UtcNow - _lastPingSample).TotalSeconds < 2.2)
            return;
        _pingInFlight = true;
        _lastPingSample = DateTime.UtcNow;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync("1.1.1.1", 1400);
                int? rtt = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : null;
                if (rtt.HasValue) _cachedPingMs = rtt.Value;

                lock (_pingWindow)
                {
                    _pingWindow.Enqueue(rtt);
                    while (_pingWindow.Count > 24) _pingWindow.Dequeue();

                    int total = _pingWindow.Count;
                    int lost = _pingWindow.Count(x => !x.HasValue);
                    _cachedPacketLossPct = total > 0 ? Math.Round(lost * 100.0 / total, 1) : 0;

                    var valid = _pingWindow.Where(x => x.HasValue).Select(x => x!.Value).ToList();
                    if (valid.Count >= 2)
                    {
                        double sumDiff = 0;
                        for (int i = 1; i < valid.Count; i++)
                            sumDiff += Math.Abs(valid[i] - valid[i - 1]);
                        _cachedJitterMs = Math.Round(sumDiff / (valid.Count - 1), 1);
                    }
                }
            }
            catch { }
            finally
            {
                _pingInFlight = false;
            }
        });
    }

    private void SampleNetwork(SystemMetrics metrics)
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();

            long totalRx = 0, totalTx = 0;
            string? primaryName = null;
            foreach (var ni in interfaces)
            {
                var stats = ni.GetIPv4Statistics();
                totalRx += stats.BytesReceived;
                totalTx += stats.BytesSent;
                primaryName ??= ni.Name;
            }

            metrics.ActiveNetworkAdapter = primaryName ?? "Bağlı değil";
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastNetSample).TotalSeconds;
            if (_lastNetBytesReceived > 0 && elapsed > 0.2)
            {
                long rxDiff = Math.Max(0, totalRx - _lastNetBytesReceived);
                long txDiff = Math.Max(0, totalTx - _lastNetBytesSent);
                metrics.NetworkDownloadMbps = Math.Round((rxDiff * 8.0) / (elapsed * 1_000_000.0), 2);
                metrics.NetworkUploadMbps = Math.Round((txDiff * 8.0) / (elapsed * 1_000_000.0), 2);
            }
            _lastNetBytesReceived = totalRx;
            _lastNetBytesSent = totalTx;
            _lastNetSample = now;
        }
        catch { }
    }

    private static void SampleDrives(SystemMetrics metrics)
    {
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                metrics.Drives.Add(new DriveMetric
                {
                    Name = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Yerel Disk" : d.VolumeLabel,
                    TotalGb = Math.Round(d.TotalSize / (1024.0 * 1024.0 * 1024.0), 1),
                    FreeGb = Math.Round(d.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1)
                });
            }
        }
        catch { }
    }

    private static double? TryReadCpuTemperature()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (var obj in searcher.Get())
            {
                if (obj["CurrentTemperature"] is uint raw)
                {
                    double celsius = (raw / 10.0) - 273.15;
                    if (celsius > 10 && celsius < 120)
                        return Math.Round(celsius, 1);
                }
            }
        }
        catch { }
        return null;
    }

    public static string GetActivePowerPlanName()
    {
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid) == 0 && pGuid != IntPtr.Zero)
            {
                try
                {
                    var guid = Marshal.PtrToStructure<Guid>(pGuid);
                    return guid.ToString().ToLowerInvariant() switch
                    {
                        "381b4222-f694-41f0-9685-ff5bb260df2e" => "Dengeli",
                        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Yüksek Performans",
                        "a1841308-3541-4fab-bc81-f71556f20b4a" => "Güç Tasarrufu",
                        "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Nihai Performans",
                        _ => "Özel Plan"
                    };
                }
                finally
                {
                    NativeMethods.LocalFree(pGuid);
                }
            }
        }
        catch { }
        return "Dengeli";
    }

    private static void ComputeHealthScore(SystemMetrics m, int highRiskFindings, int mediumRiskFindings)
    {
        int score = 100;
        if (m.RamUsagePercent > 85) score -= 20;
        else if (m.RamUsagePercent > 75) score -= 10;

        if (m.CpuUsagePercent > 85) score -= 15;
        else if (m.CpuUsagePercent > 65) score -= 7;

        foreach (var d in m.Drives)
        {
            if (d.UsedPercent > 92) score -= 15;
            else if (d.UsedPercent > 85) score -= 7;
        }

        if (highRiskFindings > 0)
            score -= Math.Min(45, highRiskFindings * 16);
        if (mediumRiskFindings > 0)
            score -= Math.Min(15, mediumRiskFindings * 3);

        m.HealthScore = Math.Clamp(score, 10, 100);
        if (highRiskFindings > 0)
        {
            m.HealthStatusText = $"Dikkat ({highRiskFindings} Yüksek Risk)";
        }
        else
        {
            m.HealthStatusText = m.HealthScore switch
            {
                >= 85 => "Mükemmel",
                >= 65 => "İyi (Optimizasyon Önerilir)",
                >= 45 => "Yük Altında",
                _ => "Müdahale Gerekli"
            };
        }
    }

    public List<ProcessItem> GetTopProcesses(int maxCount = 100, string sortBy = "RAM", Func<string, bool>? isFirewallBlocked = null)
    {
        var list = new List<ProcessItem>();
        var now = DateTime.UtcNow;
        int cores = Environment.ProcessorCount;
        var tcpByPid = GetActiveTcpConnectionsByPid();
        if (IsElevated)
            _lastNetRates = _procNetMonitor.CaptureRatesAndReset();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                double cpuPct = 0;
                var totalCpu = proc.TotalProcessorTime;
                if (_prevProcCpu.TryGetValue(proc.Id, out var prev))
                {
                    var elapsedMs = (now - prev.SampleTime).TotalMilliseconds;
                    if (elapsedMs > 50)
                    {
                        cpuPct = (totalCpu - prev.CpuTime).TotalMilliseconds / (elapsedMs * cores) * 100.0;
                        cpuPct = Math.Round(Math.Clamp(cpuPct, 0, 100), 1);
                    }
                }
                _prevProcCpu[proc.Id] = (totalCpu, now);

                double diskKbPerSec = 0;
                try
                {
                    if (NativeMethods.GetProcessIoCounters(proc.Handle, out var io))
                    {
                        ulong totalIo = io.ReadTransferCount + io.WriteTransferCount;
                        if (_prevProcIo.TryGetValue(proc.Id, out var prevIo))
                        {
                            double sec = (now - prevIo.SampleTime).TotalSeconds;
                            if (sec > 0.1 && totalIo >= prevIo.IoBytes)
                                diskKbPerSec = Math.Round((totalIo - prevIo.IoBytes) / 1024.0 / sec, 1);
                        }
                        _prevProcIo[proc.Id] = (totalIo, now);
                    }
                }
                catch { }

                tcpByPid.TryGetValue(proc.Id, out var netInfo);
                _lastNetRates.TryGetValue(proc.Id, out var netRate);

                string path = "";
                string desc = "";
                try
                {
                    path = proc.MainModule?.FileName ?? "";
                    desc = proc.MainModule?.FileVersionInfo.FileDescription ?? "";
                }
                catch { }

                bool isSelf = proc.Id == Environment.ProcessId;
                bool signed = true;
                if (!isSelf && !string.IsNullOrEmpty(path) && !path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
                {
                    signed = _signatureCache.GetOrAdd(path, NativeMethods.VerifyFileOrCatalogSignature);
                }

                list.Add(new ProcessItem
                {
                    Pid = proc.Id,
                    Name = proc.ProcessName,
                    Description = isSelf ? "KOCHAN Sentinel (Kendi Uygulaman)" : (string.IsNullOrWhiteSpace(desc) ? proc.ProcessName : desc),
                    ExecutablePath = path,
                    CpuPercent = cpuPct,
                    RamMb = Math.Round(proc.WorkingSet64 / (1024.0 * 1024.0), 1),
                    DiskKbPerSec = diskKbPerSec,
                    NetDownloadKbPerSec = netRate.DownKb,
                    NetUploadKbPerSec = netRate.UpKb,
                    ActiveConnections = netInfo.Count,
                    RemoteEndpoints = netInfo.Endpoints ?? "",
                    Priority = SafeGetPriority(proc),
                    IsSystemCritical = CriticalSystemProcesses.Contains(proc.ProcessName) || proc.Id <= 4 || isSelf,
                    IsSigned = signed,
                    IsFirewallBlocked = !isSelf && (isFirewallBlocked?.Invoke(proc.ProcessName) ?? false),
                    Category = isSelf ? "KOCHAN" : ClassifyProcess(proc.ProcessName, path)
                });
            }
            catch
            {
                // Access denied or process exited
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Clean up exited PIDs from cache
        if (_prevProcCpu.Count > 500)
        {
            var activePids = list.Select(x => x.Pid).ToHashSet();
            foreach (var key in _prevProcCpu.Keys.Where(k => !activePids.Contains(k)).ToList())
                _prevProcCpu.Remove(key);
            foreach (var key in _prevProcIo.Keys.Where(k => !activePids.Contains(k)).ToList())
                _prevProcIo.Remove(key);
        }

        IEnumerable<ProcessItem> sorted = sortBy switch
        {
            "CPU" => list.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.RamMb),
            "Disk" => list.OrderByDescending(p => p.DiskKbPerSec).ThenByDescending(p => p.RamMb),
            "Network" => list.OrderByDescending(p => p.NetDownloadKbPerSec + p.NetUploadKbPerSec).ThenByDescending(p => p.ActiveConnections).ThenByDescending(p => p.RamMb),
            _ => list.OrderByDescending(p => p.RamMb).ThenByDescending(p => p.CpuPercent)
        };

        return sorted.Take(maxCount).ToList();
    }

    private static Dictionary<int, (int Count, string Endpoints)> GetActiveTcpConnectionsByPid()
    {
        var map = new Dictionary<int, List<string>>();
        CollectTcpTable(map, isIPv6: false);
        CollectTcpTable(map, isIPv6: true);
        return map.ToDictionary(kv => kv.Key, kv => (kv.Value.Count, string.Join(", ", kv.Value.Take(3))));
    }

    private static void CollectTcpTable(Dictionary<int, List<string>> map, bool isIPv6)
    {
        int af = isIPv6 ? NativeMethods.AF_INET6 : NativeMethods.AF_INET;
        int bufferSize = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, af, NativeMethods.TCP_TABLE_OWNER_PID_ALL);
        if (bufferSize <= 0) return;

        IntPtr tcpTablePtr = Marshal.AllocHGlobal(bufferSize);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(tcpTablePtr, ref bufferSize, false, af, NativeMethods.TCP_TABLE_OWNER_PID_ALL) != 0)
                return;

            int rowCount = Marshal.ReadInt32(tcpTablePtr);
            IntPtr rowPtr = tcpTablePtr + 4;

            if (!isIPv6)
            {
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(rowPtr);
                    rowPtr += rowSize;

                    if (row.dwState == 5 && row.dwRemoteAddr != 0 && row.dwRemoteAddr != 0x0100007F)
                    {
                        AddEndpoint(map, (int)row.dwOwningPid, new System.Net.IPAddress(row.dwRemoteAddr).ToString(), row.dwRemotePort);
                    }
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(rowPtr);
                    rowPtr += rowSize;

                    if (row.dwState == 5 && row.ucRemoteAddr != null)
                    {
                        var ip = new System.Net.IPAddress(row.ucRemoteAddr);
                        if (!System.Net.IPAddress.IsLoopback(ip) && !ip.Equals(System.Net.IPAddress.IPv6Any) && !ip.Equals(System.Net.IPAddress.IPv6None))
                            AddEndpoint(map, (int)row.dwOwningPid, $"[{ip}]", row.dwRemotePort);
                    }
                }
            }
        }
        catch { }
        finally
        {
            Marshal.FreeHGlobal(tcpTablePtr);
        }
    }

    private static void AddEndpoint(Dictionary<int, List<string>> map, int pid, string ipStr, uint netPort)
    {
        ushort port = (ushort)System.Net.IPAddress.NetworkToHostOrder((short)netPort);
        if (!map.TryGetValue(pid, out var list))
        {
            list = new List<string>();
            map[pid] = list;
        }
        string ep = $"{ipStr}:{port}";
        if (!list.Contains(ep)) list.Add(ep);
    }

    private static string SafeGetPriority(Process proc)
    {
        try { return proc.PriorityClass.ToString(); }
        catch { return "Normal"; }
    }

    private static string ClassifyProcess(string name, string path)
    {
        if (CriticalSystemProcesses.Contains(name)) return "Sistem";
        if (path.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return "Windows";
        if (name.Contains("chrome", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("msedge", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("firefox", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("opera", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("brave", StringComparison.OrdinalIgnoreCase))
            return "Tarayıcı";
        if (path.Contains(@"\Steam\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Riot Games\", StringComparison.OrdinalIgnoreCase))
            return "Oyun / Launcher";
        return "Uygulama";
    }

    public bool SetEfficiencyMode(int pid, bool enable)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (enable)
                proc.PriorityClass = ProcessPriorityClass.Idle;

            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            return NativeMethods.SetProcessInformation(
                proc.Handle,
                NativeMethods.ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            return false;
        }
    }

    public bool SuspendProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return NativeMethods.NtSuspendProcess(proc.Handle) == 0;
        }
        catch { return false; }
    }

    public bool ResumeProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return NativeMethods.NtResumeProcess(proc.Handle) == 0;
        }
        catch { return false; }
    }

    public bool SetProcessPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.PriorityClass = priority;
            return true;
        }
        catch { return false; }
    }

    public bool KillProcess(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _cpuCounter?.Dispose();
        _diskReadCounter?.Dispose();
        _diskWriteCounter?.Dispose();
        _diskTimeCounter?.Dispose();
        _fpsMonitor.Dispose();
        _procNetMonitor.Dispose();
        _sensorService.Dispose();
    }
}
