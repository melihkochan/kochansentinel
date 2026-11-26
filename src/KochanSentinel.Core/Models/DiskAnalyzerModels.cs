using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KochanSentinel.Core.Models;

public sealed class DiskNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsSummaryPlaceholder { get; set; }
    public bool IsProtectedSystem { get; set; }
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public double PercentOfParent { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public DateTime LastModified { get; set; }

    public string SizeFormatted => JunkCategory.FormatBytes(SizeBytes);
    public string PercentText => $"{PercentOfParent:F1}%";
    public string DateText => LastModified == DateTime.MinValue ? "—" : LastModified.ToString("dd.MM.yyyy HH:mm");
    public string ItemsSummary => IsDirectory ? $"{FileCount:N0} dosya • {FolderCount:N0} klasör" : (string.IsNullOrEmpty(Extension) ? "Dosya" : Extension);

    public ObservableCollection<DiskNode> Children { get; } = new();
}

public sealed class DiskExtensionStat
{
    public string CategoryName { get; set; } = "";
    public string Description { get; set; } = "";
    public long TotalBytes { get; set; }
    public int FileCount { get; set; }
    public double Percent { get; set; }
    public string AccentColor { get; set; } = "#38BDF8";

    public string SizeFormatted => JunkCategory.FormatBytes(TotalBytes);
    public string PercentFormatted => $"%{Percent:F1}";
}

public sealed class DiskScanProgress
{
    public string CurrentPath { get; set; } = "";
    public int ScannedFiles { get; set; }
    public int ScannedFolders { get; set; }
    public long ScannedBytes { get; set; }
    public string ScannedSizeFormatted => JunkCategory.FormatBytes(ScannedBytes);
}

public sealed class DiskScanSummary
{
    public string RootPath { get; set; } = "";
    public long TotalScannedBytes { get; set; }
    public int TotalFiles { get; set; }
    public int TotalFolders { get; set; }
    public double ElapsedSeconds { get; set; }
    public DiskNode RootNode { get; set; } = new();
    public List<DiskNode> TopLargestFiles { get; set; } = new();
    public List<DiskExtensionStat> ExtensionStats { get; set; } = new();

    public string TotalSizeFormatted => JunkCategory.FormatBytes(TotalScannedBytes);
}

public sealed class DuplicateFileItem
{
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsOriginalKeeper { get; set; }
    public string SizeFormatted => JunkCategory.FormatBytes(SizeBytes);
    public string RoleBadge => IsOriginalKeeper ? "Asıl (Korunur)" : "Kopya";
}

public sealed class DuplicateFileGroup
{
    public string Sha256Short { get; set; } = "";
    public string FileNameSample { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public List<DuplicateFileItem> Files { get; set; } = new();
    public int CopyCount => Files.Count;
    public long WastedBytes => Math.Max(0, (long)(Files.Count - 1) * FileSizeBytes);
    public string FileSizeFormatted => JunkCategory.FormatBytes(FileSizeBytes);
    public string WastedFormatted => JunkCategory.FormatBytes(WastedBytes);
    public string PathsPreview => string.Join("  |  ", Files.ConvertAll(f => f.FullPath));
}
