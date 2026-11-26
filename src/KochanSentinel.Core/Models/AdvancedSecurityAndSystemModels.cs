using System;
using System.Collections.Generic;

namespace KochanSentinel.Core.Models;

public sealed class KernelDriverItem
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string State { get; set; } = "";
    public bool IsSigned { get; set; }
    public bool IsOutsideSystem32 { get; set; }
    public bool IsKnownVulnerableByovd { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string Reason { get; set; } = "";
}

public sealed class NetworkConnectionEntry
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string RemoteIp { get; set; } = "";
    public int RemotePort { get; set; }
    public string CountryOrOrg { get; set; } = "Sorgulanıyor...";
    public bool IsSuspicious { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string Reason { get; set; } = "";
    public string EndpointText => $"{RemoteIp}:{RemotePort}";
}

public sealed class FirewallRuleItem
{
    public string RuleName { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ProgramPath { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class DnsCacheEntry
{
    public string Domain { get; set; } = "";
    public string RecordData { get; set; } = "";
    public string RecordType { get; set; } = "";
    public bool IsSuspicious { get; set; }
}

public sealed class LoadedDllModuleInfo
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string ModuleName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long SizeKb { get; set; }
    public bool IsSigned { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class BrowserExtensionIssue
{
    public string Browser { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Name { get; set; } = "";
    public string ExtensionId { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public string RiskReason { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Medium;
}

public sealed class SmartDriveHealth
{
    public string Name { get; set; } = "";
    public double? TemperatureC { get; set; }
    public double? RemainingLifePercent { get; set; }
    public double? TotalWrittenGb { get; set; }
    public bool PredictFailure { get; set; }
    public string SummaryText { get; set; } = "Sağlıklı";
}

public sealed class SystemTweakItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsApplied { get; set; }
    public string StatusBadge => IsApplied ? "AKTİF (Optimize)" : "Varsayılan";
    public string ActionText => IsApplied ? "Varsayılana Döndür" : "Uygula";
}

public sealed class StartupAppItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string Source { get; set; } = ""; // HKCU Run, HKLM Run, Startup Klasörü, Görev Zamanlayıcı
    public string RegistryHive { get; set; } = "";
    public string RegistrySubKey { get; set; } = "";
    public string ApprovedSubKey { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsSigned { get; set; }
    public string ImpactLevel { get; set; } = "Orta"; // Yüksek, Orta, Düşük
    public string StatusText => IsEnabled ? "Aktif" : "Devre Dışı";
    public string ToggleButtonText => IsEnabled ? "Devre Dışı Bırak" : "Etkinleştir";
}

public sealed class BootPerformanceInfo
{
    public double? LastBootSeconds { get; set; }
    public double? MainPathSeconds { get; set; }
    public double? PostBootSeconds { get; set; }
    public DateTime? BootTime { get; set; }
    public string SummaryText { get; set; } = "Önyükleme verisi okunamadı (Windows Diagnostics log kapalı olabilir).";
}

public sealed class DnsProfileItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string PrimaryDns { get; set; } = "";
    public string SecondaryDns { get; set; } = "";
    public string DohTemplate { get; set; } = "";
    public bool IsAutomatic { get; set; }
    public string ServersLabel => IsAutomatic ? "Otomatik (DHCP / ISP)" : $"{PrimaryDns} / {SecondaryDns}";
}

public sealed class UpdateCheckResult
{
    public bool HasUpdate { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string ExeDownloadUrl { get; set; } = "";
    public string Sha256FileUrl { get; set; } = "";
    public long ExeSizeBytes { get; set; }
    public string Message { get; set; } = "";
}

public sealed class UpdateDownloadProgress
{
    public double Percent { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string StatusText { get; set; } = "";
}

// --- Hardware Driver & Windows Update Audit Models ---
public sealed class DriverAuditItem
{
    public string DeviceName { get; set; } = "";
    public string DeviceClass { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string DriverDateText { get; set; } = "—";
    public string StatusBadge { get; set; } = "Güncel";
    public string Recommendation { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public bool IsPendingWindowsUpdate { get; set; }
    public string UpdateId { get; set; } = "";
}

// --- Monitor & Color Calibration Models ---
public sealed class MonitorDisplayInfo
{
    public string DeviceName { get; set; } = ""; // e.g. \\.\DISPLAY1
    public string FriendlyName { get; set; } = "";
    public bool IsPrimary { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MaxWidth { get; set; }
    public int MaxHeight { get; set; }
    public int CurrentRefreshHz { get; set; }
    public int MaxRefreshHz { get; set; }
    public int BitsPerPixel { get; set; }
    public string IccProfileName { get; set; } = "Varsayılan (Profil Yok)";
    public bool SupportsDdcBrightness { get; set; }
    public int CurrentBrightness { get; set; } = -1;
    public int CurrentContrast { get; set; } = -1;
    public bool NeedsRefreshRateBoost => MaxRefreshHz > CurrentRefreshHz && MaxRefreshHz >= 75;
    public string ResolutionSummary => $"{Width}x{Height} @ {CurrentRefreshHz} Hz (Maks: {MaxWidth}x{MaxHeight} @ {MaxRefreshHz} Hz)";
    public string RefreshStatusBadge => NeedsRefreshRateBoost ? $"⚠ {CurrentRefreshHz}Hz → {MaxRefreshHz}Hz Yapılabilir!" : $"✓ {CurrentRefreshHz} Hz (Optimum)";
    public List<string> Recommendations { get; set; } = new();
    public string RecommendationsText => string.Join("\n• ", Recommendations);
}

public sealed class ColorProfilePreset
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double Gamma { get; set; } = 1.0;
    public double RedScale { get; set; } = 1.0;
    public double GreenScale { get; set; } = 1.0;
    public double BlueScale { get; set; } = 1.0;
    public double BrightnessLift { get; set; } = 0.0;
}

// --- Unused Windows Services & Background Bloat Audit Models ---
public sealed class ServiceOptimizationItem
{
    public string ServiceName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string CurrentStatus { get; set; } = ""; // Çalışıyor / Durduruldu
    public string CurrentStartMode { get; set; } = ""; // Automatic / Manual / Disabled
    public string RecommendedStartMode { get; set; } = "Manual"; // Manual / Disabled
    public string DefaultStartMode { get; set; } = "Manual";
    public bool IsOptimized { get; set; }
    public FindingSeverity ImpactSeverity { get; set; } = FindingSeverity.Low;
    public string StatusBadge => IsOptimized ? "✓ Optimize" : $"{CurrentStatus} ({CurrentStartMode})";
    public string ActionText => IsOptimized ? "Varsayılana Döndür" : $"Önerilene Al ({RecommendedStartMode})";
}

public sealed class BloatProcessAuditItem
{
    public int Pid { get; set; }
    public string ProcessName { get; set; } = "";
    public string Category { get; set; } = ""; // Gereksiz Arka Plan / Riskli & İmzasız
    public string Reason { get; set; } = "";
    public double RamMb { get; set; }
    public double CpuPercent { get; set; }
    public bool IsRisky { get; set; }
    public FindingSeverity Severity { get; set; } = FindingSeverity.Low;
}

public sealed class OpenSourceToolReference
{
    public string Name { get; set; } = "";
    public string RepoSlug { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url => $"https://github.com/{RepoSlug}";
}
