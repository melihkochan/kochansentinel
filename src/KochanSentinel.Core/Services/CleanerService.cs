using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class CleanerService
{
    public async Task<List<JunkCategory>> ScanJunkAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

            var categories = new List<JunkCategory>
            {
                new()
                {
                    Id = "user_temp",
                    Name = "Kullanıcı Geçici Dosyaları (%TEMP%)",
                    Description = "Kurulum ve uygulama artıkları, geçici önbellekler.",
                    TargetPaths = { Path.GetTempPath() }
                },
                new()
                {
                    Id = "win_temp",
                    Name = "Windows Geçici & Crash Dump",
                    Description = "Sistem temp klasörü ve eski çökme dökümleri (Minidump).",
                    RequiresElevation = true,
                    TargetPaths =
                    {
                        Path.Combine(winDir, "Temp"),
                        Path.Combine(winDir, "Minidump"),
                        Path.Combine(localApp, "CrashDumps")
                    }
                },
                new()
                {
                    Id = "browser_cache",
                    Name = "Tarayıcı Önbellekleri (Chrome, Edge, Brave, Opera, Firefox)",
                    Description = "Tüm profillerdeki önbellek (Cache) silinir; şifreler, yer imleri ve geçmiş korunur.",
                    TargetPaths = DiscoverBrowserCachePaths(localApp, appData)
                },
                new()
                {
                    Id = "app_cache",
                    Name = "Discord, Steam & Spotify Önbelleği",
                    Description = "Oyun başlatıcıları ve sohbet uygulamalarının biriken medya önbellekleri.",
                    TargetPaths =
                    {
                        Path.Combine(appData, @"discord\Cache\Cache_Data"),
                        Path.Combine(appData, @"discord\Code Cache"),
                        Path.Combine(localApp, @"Steam\htmlcache\Cache"),
                        Path.Combine(localApp, @"Spotify\Data")
                    }
                },
                new()
                {
                    Id = "win_update",
                    Name = "Windows Update İndirme Önbelleği",
                    Description = "Eski Windows güncelleme kurulum dosyaları.",
                    RequiresElevation = true,
                    IsSelected = false,
                    TargetPaths = { Path.Combine(winDir, @"SoftwareDistribution\Download") }
                },
                new()
                {
                    Id = "gpu_shader",
                    Name = "GPU Shader Önbelleği (NVIDIA / AMD)",
                    Description = "Oyunlarda takılma varsa sıfırlamak için kullanılır (sonraki açılışta yeniden derlenir).",
                    IsSelected = false,
                    TargetPaths =
                    {
                        Path.Combine(localApp, @"NVIDIA\DXCache"),
                        Path.Combine(localApp, @"NVIDIA\GLCache"),
                        Path.Combine(localApp, @"AMD\DxCache")
                    }
                },
                new()
                {
                    Id = "recycle_bin",
                    Name = "Geri Dönüşüm Kutusu",
                    Description = "Silinmiş dosyaların kapladığı alanı tamamen boşaltır.",
                    IsRecycleBin = true,
                    IsSelected = false
                }
            };

            foreach (var cat in categories)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Taranıyor: {cat.Name}...");
                if (cat.IsRecycleBin)
                {
                    // Recycle bin size is estimated across fixed drives' $Recycle.Bin if accessible
                    var (bytes, count) = MeasureRecycleBin();
                    cat.SizeBytes = bytes;
                    cat.FileCount = count;
                    continue;
                }

                long totalBytes = 0;
                int totalFiles = 0;
                foreach (var dir in cat.TargetPaths.Where(Directory.Exists))
                {
                    progress?.Report($"Taranıyor: {dir}");
                    var (b, c) = MeasureDirectory(dir, ct);
                    totalBytes += b;
                    totalFiles += c;
                }
                cat.SizeBytes = totalBytes;
                cat.FileCount = totalFiles;
            }

            return categories;
        }, ct);
    }

    public async Task<CleanResult> CleanAsync(IEnumerable<JunkCategory> selectedCategories, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var result = new CleanResult();

            foreach (var cat in selectedCategories.Where(c => c.IsSelected))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Temizleniyor: {cat.Name}");

                if (cat.IsRecycleBin)
                {
                    try
                    {
                        long before = cat.SizeBytes;
                        int hr = NativeMethods.SHEmptyRecycleBin(
                            IntPtr.Zero,
                            null,
                            NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);

                        if (hr == 0)
                        {
                            result.FreedBytes += before;
                            result.DeletedFiles += cat.FileCount;
                            result.Notes.Add("Geri Dönüşüm Kutusu boşaltıldı.");
                            progress?.Report("Geri Dönüşüm Kutusu boşaltıldı.");
                        }
                    }
                    catch { }
                    continue;
                }

                foreach (var dir in cat.TargetPaths.Where(Directory.Exists))
                {
                    progress?.Report($"Klasör temizleniyor: {dir}");
                    CleanDirectory(dir, result, ct);
                }
            }

            return result;
        }, ct);
    }

    private static (long Bytes, int Count) MeasureDirectory(string path, CancellationToken ct)
    {
        long bytes = 0;
        int count = 0;
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in Directory.EnumerateFiles(path, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                    count++;
                }
                catch { }
            }
        }
        catch { }
        return (bytes, count);
    }

    private static List<string> DiscoverBrowserCachePaths(string localApp, string appData)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Multi-profile Chromium User Data roots (Default, Profile 1, Profile 2, Guest Profile...)
        var chromiumRoots = new[]
        {
            Path.Combine(localApp, @"Google\Chrome\User Data"),
            Path.Combine(localApp, @"Microsoft\Edge\User Data"),
            Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data"),
            Path.Combine(localApp, @"Vivaldi\User Data"),
            Path.Combine(appData, @"Opera Software\Opera Stable"),
            Path.Combine(appData, @"Opera Software\Opera GX Stable")
        };

        foreach (var root in chromiumRoots.Where(Directory.Exists))
        {
            try
            {
                // Opera stores cache directly inside root, Chromium uses Default / Profile *
                foreach (var candidateSub in new[] { @"Cache\Cache_Data", "Code Cache" })
                {
                    var direct = Path.Combine(root, candidateSub);
                    if (Directory.Exists(direct)) paths.Add(direct);
                }

                foreach (var profDir in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(profDir);
                    if (!name.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var sub in new[] { @"Cache\Cache_Data", "Code Cache", @"GPUCache" })
                    {
                        var full = Path.Combine(profDir, sub);
                        if (Directory.Exists(full)) paths.Add(full);
                    }
                }
            }
            catch { }
        }

        // 2. Mozilla Firefox Profiles (LocalAppData cache2 + AppData startupCache)
        foreach (var ffBase in new[]
        {
            Path.Combine(localApp, @"Mozilla\Firefox\Profiles"),
            Path.Combine(appData, @"Mozilla\Firefox\Profiles")
        }.Where(Directory.Exists))
        {
            try
            {
                foreach (var prof in Directory.EnumerateDirectories(ffBase))
                {
                    foreach (var sub in new[] { @"cache2\entries", "cache2", "startupCache" })
                    {
                        var full = Path.Combine(prof, sub);
                        if (Directory.Exists(full))
                        {
                            paths.Add(full);
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        return paths.ToList();
    }

    private static (long Bytes, int Count) MeasureRecycleBin()
    {
        long bytes = 0;
        int count = 0;
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
            {
                var rbPath = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                if (Directory.Exists(rbPath))
                {
                    var (b, c) = MeasureDirectory(rbPath, CancellationToken.None);
                    bytes += b;
                    count += c;
                }
            }
        }
        catch { }
        return (bytes, count);
    }

    private static void CleanDirectory(string dirPath, CleanResult result, CancellationToken ct)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var fi = new FileInfo(file);
                    long len = fi.Length;
                    if (fi.IsReadOnly) fi.IsReadOnly = false;
                    fi.Delete();
                    result.FreedBytes += len;
                    result.DeletedFiles++;
                }
                catch
                {
                    result.SkippedLockedFiles++;
                }
            }

            foreach (var subDir in Directory.EnumerateDirectories(dirPath))
            {
                ct.ThrowIfCancellationRequested();
                if ((File.GetAttributes(subDir) & FileAttributes.ReparsePoint) != 0)
                    continue;

                CleanDirectory(subDir, result, ct);
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                        Directory.Delete(subDir, false);
                }
                catch { }
            }
        }
        catch { }
    }
}
