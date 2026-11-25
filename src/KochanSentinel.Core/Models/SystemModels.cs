using System;
using System.Collections.Generic;

namespace KochanSentinel.Core.Models;

public sealed class SystemMetrics
{
    public double CpuUsagePercent { get; set; }
    public string CpuName { get; set; } = "Bilinmiyor";
    public int CpuCores { get; set; }
    public int CpuLogicalProcessors { get; set; }

    public double RamUsedGb { get; set; }
    public double RamTotalGb { get; set; }
    public double RamAvailableGb { get; set; }
    public double RamUsagePercent { get; set; }

    public double DiskUsagePercent { get; set; }
    public double DiskReadMbps { get; set; }
    public double DiskWriteMbps { get; set; }

    public double NetworkDownloadMbps { get; set; }
    public double NetworkUploadMbps { get; set; }
    public int? PingMs { get; set; }
    public double PingJitterMs { get; set; }
    public double PacketLossPercent { get; set; }
    public int? CurrentFps { get; set; }
    public string ActiveNetworkAdapter { get; set; } = "Bağlı değil";

    public string GpuName { get; set; } = "Algılanmadı";
    public double? CpuTempCelsius { get; set; }
    public double? GpuTempCelsius { get; set; }
    public double? GpuLoadPercent { get; set; }
    public double? GpuVramUsedMb { get; set; }
    public double? GpuVramTotalMb { get; set; }
    public int? FanRpm { get; set; }

    public TimeSpan Uptime { get; set; }
    public int ActiveProcessCount { get; set; }
    public string ActivePowerPlan { get; set; } = "Dengeli";
    public bool IsElevated { get; set; }

    public List<DriveMetric> Drives { get; set; } = new();
    public List<SmartDriveHealth> SmartDrives { get; set; } = new();
    public int HealthScore { get; set; } = 100;
    public string HealthStatusText { get; set; } = "Mükemmel";
}

public sealed class DriveMetric
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedGb => Math.Max(0, TotalGb - FreeGb);
    public double UsedPercent => TotalGb > 0 ? Math.Round(UsedGb / TotalGb * 100.0, 1) : 0;
}

public sealed class ProcessItem
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public double CpuPercent { get; set; }
    public double RamMb { get; set; }
    public double DiskKbPerSec { get; set; }
    public int ActiveConnections { get; set; }
    public string RemoteEndpoints { get; set; } = "";
    public string Priority { get; set; } = "Normal";
    public bool IsSystemCritical { get; set; }
    public bool IsSigned { get; set; }
    public bool IsFirewallBlocked { get; set; }
    public string Category { get; set; } = "Uygulama";
}
