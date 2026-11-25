using System;

namespace KochanSentinel.Core.Models;

public sealed class BenchmarkResult
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimestampText => Timestamp.ToString("dd.MM.yyyy HH:mm");

    public string CpuName { get; set; } = "";
    public string GpuName { get; set; } = "";
    public string TargetDrive { get; set; } = "C:\\";
    public double TotalRamGb { get; set; }

    // CPU
    public int CpuSingleScore { get; set; }
    public int CpuMultiScore { get; set; }
    public double CpuScalingRatio { get; set; }
    public double CpuSingleMops { get; set; }
    public double CpuMultiMops { get; set; }

    // RAM
    public int RamScore { get; set; }
    public double RamBandwidthGbPerSec { get; set; }
    public double RamLatencyNs { get; set; }

    // Storage
    public int DiskScore { get; set; }
    public double DiskSeqReadMbps { get; set; }
    public double DiskSeqWriteMbps { get; set; }
    public int DiskRandom4kReadIops { get; set; }
    public int DiskRandom4kWriteIops { get; set; }

    // Composite UserBenchmark-style profiles (100% = Modern Balanced Desktop Reference)
    public int OverallScore { get; set; }
    public int GamingPercent { get; set; }
    public int DesktopPercent { get; set; }
    public int WorkstationPercent { get; set; }
    public string TierBadge { get; set; } = "—";
    public string BottleneckNote { get; set; } = "";
}

public sealed class BenchmarkProgress
{
    public string StageName { get; set; } = "";
    public string Detail { get; set; } = "";
    public int PercentComplete { get; set; }
}
