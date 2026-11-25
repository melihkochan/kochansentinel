using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// High-speed TreeSize-style disk analyzer that safely traverses directories without falling into
/// NTFS junction/reparse loops, building a sorted folder tree, top-largest-file list, and type breakdown.
/// </summary>
public sealed class DiskAnalyzerService
{
    private const int MaxTreeChildrenPerNode = 75;
    private const int MaxTopFiles = 60;

    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".ts", ".m4v" };

    private static readonly HashSet<string> GameArchiveExts = new(StringComparer.OrdinalIgnoreCase)
        { ".pak", ".vpk", ".rpf", ".zip", ".rar", ".7z", ".iso", ".img", ".vhdx", ".vhd", ".uasset", ".ucas", ".utoc", ".upk", ".bin" };

    private static readonly HashSet<string> AppSystemExts = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".dll", ".msi", ".msix", ".sys", ".cab", ".dmp", ".log", ".etl", ".tmp" };

    private static readonly HashSet<string> MediaImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".psd", ".raw", ".cr2", ".svg", ".mp3", ".wav", ".flac" };

    private sealed class CategoryAccumulator
    {
        public long Bytes;
        public int Count;
    }

    public async Task<DiskScanSummary> AnalyzeAsync(
        string rootPath,
        IProgress<DiskScanProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                rootPath = @"C:\";

            int totalFiles = 0;
            int totalFolders = 0;
            long totalBytes = 0;
            long lastReportTicks = Environment.TickCount64;

            var topFilesHeap = new PriorityQueue<DiskNode, long>();
            var catStats = new Dictionary<string, CategoryAccumulator>(StringComparer.Ordinal)
            {
                ["Oyun Varlıkları & Arşivler (.pak/.iso/.zip)"] = new(),
                ["Video & Medya (.mp4/.mkv/.avi)"] = new(),
                ["Uygulama, Sistem & Log (.exe/.dll/.dmp)"] = new(),
                ["Görsel & Ses (.png/.jpg/.psd/.wav)"] = new(),
                ["Diğer Dosyalar"] = new()
            };

            DiskNode ScanFolderRecursive(string dirPath, int depth)
            {
                ct.ThrowIfCancellationRequested();
                totalFolders++;

                string folderName = Path.GetFileName(dirPath.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(folderName)) folderName = dirPath;

                var node = new DiskNode
                {
                    Name = folderName,
                    FullPath = dirPath,
                    IsDirectory = true,
                    IsProtectedSystem = IsSystemProtectedPath(dirPath),
                    IsExpanded = depth == 0
                };

                long dirBytes = 0;
                int dirFileCount = 0;
                int dirFolderCount = 0;
                DateTime latestWrite = DateTime.MinValue;
                var rawChildren = new List<DiskNode>();

                try
                {
                    foreach (var filePath in Directory.EnumerateFiles(dirPath))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(filePath);
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            long len = fi.Length;
                            var lw = fi.LastWriteTime;
                            if (lw > latestWrite) latestWrite = lw;

                            string ext = fi.Extension;
                            dirBytes += len;
                            dirFileCount++;
                            totalFiles++;
                            totalBytes += len;

                            string catKey = ClassifyExtension(ext);
                            catStats[catKey].Bytes += len;
                            catStats[catKey].Count++;

                            // Track global Top 60 largest files
                            if (len >= 5 * 1024 * 1024 && (topFilesHeap.Count < MaxTopFiles || len > topFilesHeap.Peek().SizeBytes))
                            {
                                var fileNode = new DiskNode
                                {
                                    Name = fi.Name,
                                    FullPath = filePath,
                                    IsDirectory = false,
                                    IsProtectedSystem = IsSystemProtectedPath(filePath),
                                    Extension = string.IsNullOrEmpty(ext) ? "Dosya" : ext.ToLowerInvariant(),
                                    SizeBytes = len,
                                    LastModified = lw,
                                    FileCount = 1
                                };
                                topFilesHeap.Enqueue(fileNode, len);
                                if (topFilesHeap.Count > MaxTopFiles)
                                    topFilesHeap.Dequeue();
                            }

                            // Keep direct file children for depth <= 3 or notable files (>= 1 MB) to keep TreeView crisp
                            if (depth <= 2 || len >= 1024 * 1024)
                            {
                                rawChildren.Add(new DiskNode
                                {
                                    Name = fi.Name,
                                    FullPath = filePath,
                                    IsDirectory = false,
                                    IsProtectedSystem = IsSystemProtectedPath(filePath),
                                    Extension = string.IsNullOrEmpty(ext) ? "Dosya" : ext.ToLowerInvariant(),
                                    SizeBytes = len,
                                    LastModified = lw,
                                    FileCount = 1
                                });
                            }
                        }
                        catch { }
                    }

                    foreach (var subDir in Directory.EnumerateDirectories(dirPath))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var attrs = File.GetAttributes(subDir);
                            if ((attrs & FileAttributes.ReparsePoint) != 0)
                                continue; // Crucial: prevent NTFS junction infinite loops (AppData/Application Data)

                            var childNode = ScanFolderRecursive(subDir, depth + 1);
                            dirBytes += childNode.SizeBytes;
                            dirFileCount += childNode.FileCount;
                            dirFolderCount += 1 + childNode.FolderCount;
                            if (childNode.LastModified > latestWrite)
                                latestWrite = childNode.LastModified;

                            rawChildren.Add(childNode);
                        }
                        catch { }
                    }
                }
                catch { }

                node.SizeBytes = dirBytes;
                node.FileCount = dirFileCount;
                node.FolderCount = dirFolderCount;
                node.LastModified = latestWrite;

                // Sort largest-first & compute parent percentages
                rawChildren.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
                long keptBytes = 0;
                int kept = 0;
                foreach (var child in rawChildren)
                {
                    if (kept < MaxTreeChildrenPerNode && (child.IsDirectory || child.SizeBytes >= 512 * 1024 || rawChildren.Count <= 25))
                    {
                        child.PercentOfParent = dirBytes > 0 ? Math.Clamp(child.SizeBytes * 100.0 / dirBytes, 0, 100) : 0;
                        node.Children.Add(child);
                        keptBytes += child.SizeBytes;
                        kept++;
                    }
                }

                long remainder = Math.Max(0, dirBytes - keptBytes);
                int skippedCount = rawChildren.Count - kept;
                if (remainder > 0 && skippedCount > 0)
                {
                    node.Children.Add(new DiskNode
                    {
                        Name = $"[{skippedCount:N0} küçük öğe]",
                        FullPath = dirPath,
                        IsDirectory = false,
                        IsSummaryPlaceholder = true,
                        IsProtectedSystem = true,
                        Extension = "Özet",
                        SizeBytes = remainder,
                        PercentOfParent = dirBytes > 0 ? Math.Clamp(remainder * 100.0 / dirBytes, 0, 100) : 0
                    });
                }

                long nowTicks = Environment.TickCount64;
                if (nowTicks - lastReportTicks >= 160)
                {
                    lastReportTicks = nowTicks;
                    progress?.Report(new DiskScanProgress
                    {
                        CurrentPath = dirPath,
                        ScannedFiles = totalFiles,
                        ScannedFolders = totalFolders,
                        ScannedBytes = totalBytes
                    });
                }

                return node;
            }

            var rootNode = ScanFolderRecursive(rootPath, 0);
            rootNode.PercentOfParent = 100;

            var topList = new List<DiskNode>(topFilesHeap.Count);
            while (topFilesHeap.Count > 0)
                topList.Add(topFilesHeap.Dequeue());
            topList.Reverse();
            foreach (var f in topList)
                f.PercentOfParent = rootNode.SizeBytes > 0 ? Math.Clamp(f.SizeBytes * 100.0 / rootNode.SizeBytes, 0, 100) : 0;

            var colorMap = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Oyun Varlıkları & Arşivler (.pak/.iso/.zip)"] = "#38BDF8",
                ["Video & Medya (.mp4/.mkv/.avi)"] = "#C084FC",
                ["Uygulama, Sistem & Log (.exe/.dll/.dmp)"] = "#FBBF24",
                ["Görsel & Ses (.png/.jpg/.psd/.wav)"] = "#4ADE80",
                ["Diğer Dosyalar"] = "#71717A"
            };

            var extStats = catStats
                .Select(kv => new DiskExtensionStat
                {
                    CategoryName = kv.Key,
                    TotalBytes = kv.Value.Bytes,
                    FileCount = kv.Value.Count,
                    Percent = totalBytes > 0 ? Math.Round(kv.Value.Bytes * 100.0 / totalBytes, 1) : 0,
                    AccentColor = colorMap.GetValueOrDefault(kv.Key, "#38BDF8")
                })
                .OrderByDescending(s => s.TotalBytes)
                .ToList();

            sw.Stop();
            return new DiskScanSummary
            {
                RootPath = rootPath,
                TotalScannedBytes = totalBytes,
                TotalFiles = totalFiles,
                TotalFolders = totalFolders,
                ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                RootNode = rootNode,
                TopLargestFiles = topList,
                ExtensionStats = extStats
            };
        }, ct);
    }

    private static readonly HashSet<string> ProtectedRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"
    };

    public static bool IsSystemProtectedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var norm = path.TrimEnd('\\');
        var fileName = Path.GetFileName(norm);
        if (ProtectedRootFiles.Contains(fileName))
            return true;

        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (norm.Equals(winDir, StringComparison.OrdinalIgnoreCase) ||
            norm.StartsWith(winDir + @"\", StringComparison.OrdinalIgnoreCase))
            return true;

        var sysDrive = Path.GetPathRoot(winDir)?.TrimEnd('\\') ?? "C:";
        if (norm.Equals(sysDrive, StringComparison.OrdinalIgnoreCase) ||
            norm.Contains(@"\System Volume Information", StringComparison.OrdinalIgnoreCase) ||
            norm.Contains(@"\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals(Path.Combine(sysDrive + @"\", "Program Files"), StringComparison.OrdinalIgnoreCase) ||
            norm.Equals(Path.Combine(sysDrive + @"\", "Program Files (x86)"), StringComparison.OrdinalIgnoreCase) ||
            norm.Equals(Path.Combine(sysDrive + @"\", "Users"), StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static (bool Ok, string Message) SendItemToRecycleBin(DiskNode node)
    {
        if (node.IsSummaryPlaceholder || string.IsNullOrWhiteSpace(node.FullPath))
            return (false, "Özet öğesi doğrudan silinemez; klasörü genişletip dosya seçin.");

        if (IsSystemProtectedPath(node.FullPath))
            return (false, "Güvenlik kilidi: Windows sistem klasörleri buradan silinemez.");

        bool ok = NativeMethods.SendToRecycleBin(node.FullPath);
        return ok
            ? (true, $"{node.Name} ({node.SizeFormatted}) Geri Dönüşüm Kutusuna taşındı.")
            : (false, $"{node.Name} taşınamadı (başka bir uygulama tarafından kullanılıyor olabilir).");
    }

    private static string ClassifyExtension(string ext)
    {
        if (GameArchiveExts.Contains(ext)) return "Oyun Varlıkları & Arşivler (.pak/.iso/.zip)";
        if (VideoExts.Contains(ext)) return "Video & Medya (.mp4/.mkv/.avi)";
        if (AppSystemExts.Contains(ext)) return "Uygulama, Sistem & Log (.exe/.dll/.dmp)";
        if (MediaImageExts.Contains(ext)) return "Görsel & Ses (.png/.jpg/.psd/.wav)";
        return "Diğer Dosyalar";
    }
}
