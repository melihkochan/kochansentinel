using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KochanSentinel.Core.Models;

public sealed class JunkCategory
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsSelected { get; set; } = true;
    public bool RequiresElevation { get; set; }
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public List<string> TargetPaths { get; set; } = new();
    public bool IsRecycleBin { get; set; }

    public string PathsSummary => IsRecycleBin ? "$Recycle.Bin" : string.Join(" | ", TargetPaths);
    public string SizeFormatted => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}

public sealed class CleanResult
{
    public long FreedBytes { get; set; }
    public int DeletedFiles { get; set; }
    public int SkippedLockedFiles { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class RamOptimizeResult
{
    public double BeforeUsedGb { get; set; }
    public double AfterUsedGb { get; set; }
    public double FreedMb { get; set; }
    public int TrimmedProcesses { get; set; }
    public int EcoModeAppliedCount { get; set; }
    public bool StandbyListPurged { get; set; }
    public string Message { get; set; } = "";
}

public enum FindingSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public sealed class SecurityFinding : INotifyPropertyChanged
{
    private bool _isSelected;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Category { get; set; } = ""; // Başlangıç, Zamanlanmış Görev, Servis, Hosts, Kalıntı Dosya
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string Reason { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Low;
    public bool IsSigned { get; set; }
    public bool CanDisableOrDelete { get; set; } = true;
    public bool RequiresConfirmation { get; set; }
    public int ProcessId { get; set; }
    public string ServiceName { get; set; } = "";
    public string RegistryHive { get; set; } = "";
    public string RegistryKeyPath { get; set; } = "";
    public string RegistryValueName { get; set; } = "";
}

public sealed class FileScanResult
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public bool IsSigned { get; set; }
    public string SignerName { get; set; } = "İmzasız";
    public double ShannonEntropy { get; set; }
    public bool IsPackedOrEncrypted { get; set; }
    public List<string> SuspiciousApis { get; set; } = new();
    public List<string> HeuristicFlags { get; set; } = new();
    public string DefenderResult { get; set; } = "Taranmadı";
    public string CloudIntelResult { get; set; } = "Kontrol edilmedi";
    public FindingSeverity RiskLevel { get; set; } = FindingSeverity.Info;
    public string Verdict { get; set; } = "Temiz";
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}

public sealed class SecurityEvent
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string TimeText => Timestamp.ToString("HH:mm:ss");
}

public sealed class SecurityHistoryEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string ActionType { get; set; } = ""; // Anlık Kalkan, Manuel Tarama, Kalıntı Temizliği
    public string TargetName { get; set; } = "";
    public string Details { get; set; } = "";
    public string Verdict { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string DateTimeText => Timestamp.ToString("dd.MM.yyyy HH:mm:ss");
}

public sealed class QuarantineEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string OriginalPath { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string QuarantineFileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Reason { get; set; } = "";
    public int OriginalAttributes { get; set; }
    public DateTime QuarantinedAt { get; set; } = DateTime.Now;
    public string DateText => QuarantinedAt.ToString("dd.MM.yyyy HH:mm");
}

public sealed class GameModeStatus
{
    public bool IsEnabled { get; set; }
    public bool IsGameActive { get; set; }
    public string ActiveGameName { get; set; } = "";
    public int ActiveGamePid { get; set; }
    public string PreviousPowerPlanGuid { get; set; } = "";
    public List<int> SuspendedPids { get; set; } = new();
}

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;
    public bool StartupDefaultMigrated { get; set; } = false;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool ShowMiniHudOnStart { get; set; } = false;
    public bool AutoGameMode { get; set; } = true;
    public bool EnableTimerResolution05Ms { get; set; } = true;
    public bool EnableProBalance { get; set; } = true;
    public bool EnableAutoIslc { get; set; } = true;
    public int IslcFreeRamThresholdMb { get; set; } = 4096;
    public int IslcStandbyThresholdMb { get; set; } = 1024;
    public bool RealTimeDownloadsShield { get; set; } = true;
    public bool EnableHiddenProcessDetector { get; set; } = true;
    public bool EnableAutoQuarantine { get; set; } = true;
    public bool AutoKillMaliciousScripts { get; set; } = true;
    public bool EnableActiveRatMonitor { get; set; } = true;
    public bool EnableRansomwareHoneypot { get; set; } = true;
    public bool EnableUsbShield { get; set; } = true;
    public bool EnableSelfDefenseWatchdog { get; set; } = true;
    public bool EnableAutoRestorePoint { get; set; } = true;
    public DateTime LastAutoRestorePointUtc { get; set; } = DateTime.MinValue;
    public bool HudShowCpu { get; set; } = true;
    public bool HudShowRam { get; set; } = true;
    public bool HudShowGpu { get; set; } = true;
    public bool HudShowFps { get; set; } = true;
    public bool HudShowNet { get; set; } = true;
    public bool HudShowPing { get; set; } = true;
    public double HudOpacity { get; set; } = 0.94;
    public bool HudClickThrough { get; set; } = false;
    public bool HudCompactMode { get; set; } = false;
    public string HudHotkeyPreset { get; set; } = "Ctrl+Shift+H";
    public string WindowBackdropMode { get; set; } = "None"; // None, Mica, Acrylic
    public bool AutoCheckUpdatesOnStart { get; set; } = true;
    public bool UseSafeDriverlessSensors { get; set; } = false;
    public bool EnableClipboardClipperGuard { get; set; } = true;
    public bool RansomwareAutoKillMode { get; set; } = false; // Default: Suspend & Alert (safe against false positives)
    public bool EnableAutoMaintenanceSchedule { get; set; } = true;
    public string LastAutoMaintenanceDate { get; set; } = "";
    public bool AutoRamOptimizeOnHighUsage { get; set; } = false;
    public int RamThresholdPercent { get; set; } = 85;
    public string VirusTotalApiKey { get; set; } = "";
    public string MalwareBazaarAuthKey { get; set; } = "";
    public string DiscordWebhookUrl { get; set; } = "";
    public List<string> TrustedWhitelist { get; set; } = new();
    public List<string> CustomGameExecutables { get; set; } = new();
    public List<string> GameModeSuppressList { get; set; } = new()
    {
        "OneDrive", "SearchIndexer", "AdobeUpdateService", "CCXProcess",
        "EpicWebHelper", "OriginWebHelperService", "YourPhone", "PhoneExperienceHost"
    };
}
