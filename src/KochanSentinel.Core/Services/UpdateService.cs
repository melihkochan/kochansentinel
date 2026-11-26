using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Built-in GitHub Releases Self-Updater for single-file KochanSentinel.exe.
/// Supports silent startup checks, release notes, streaming download with progress,
/// SHA-256 verification against SHA256SUMS.txt, and safe self-replacement.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(4) };

    public static string GetCurrentAppVersion()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            var infoVer = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(infoVer))
            {
                // Strip +commitSha metadata if SourceLink added one
                int plus = infoVer.IndexOf('+');
                var clean = plus > 0 ? infoVer[..plus] : infoVer;
                if (Version.TryParse(clean.TrimStart('v', 'V'), out _))
                    return clean.TrimStart('v', 'V');
            }
            var v = entry?.GetName().Version;
            if (v != null && (v.Major > 0 || v.Minor > 0))
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
        }
        catch { }
        return "1.1.0";
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(string repoSlug, CancellationToken ct = default)
    {
        string currentVer = GetCurrentAppVersion();
        if (string.IsNullOrWhiteSpace(repoSlug)) repoSlug = "melihkochan/kochansentinel";
        repoSlug = repoSlug.Trim().Trim('/');

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repoSlug}/releases/latest");
            req.Headers.Add("User-Agent", $"KochanSentinel/{currentVer}");
            req.Headers.Add("Accept", "application/vnd.github+json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await Http.SendAsync(req, cts.Token);

            if ((int)resp.StatusCode == 404)
            {
                return new UpdateCheckResult
                {
                    HasUpdate = false,
                    CurrentVersion = currentVer,
                    LatestVersion = currentVer,
                    ReleaseUrl = $"https://github.com/{repoSlug}/releases",
                    Message = $"Güncel sürüm kullanılıyor (v{currentVer}). Henüz GitHub Release yayınlanmamış."
                };
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    HasUpdate = false,
                    CurrentVersion = currentVer,
                    LatestVersion = currentVer,
                    Message = $"GitHub sürüm kontrolü başarısız (HTTP {(int)resp.StatusCode})."
                };
            }

            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "") : "";
            string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
            string body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "";
            string htmlUrl = root.TryGetProperty("html_url", out var uEl) ? (uEl.GetString() ?? "") : "";

            string exeUrl = "";
            string shaUrl = "";
            long exeSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string assetName = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                    string dlUrl = asset.TryGetProperty("browser_download_url", out var du) ? (du.GetString() ?? "") : "";
                    long sz = asset.TryGetProperty("size", out var szEl) && szEl.TryGetInt64(out var parsedSz) ? parsedSz : 0;

                    if (assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeUrl = dlUrl;
                        exeSize = sz;
                    }
                    else if (assetName.Contains("SHA256", StringComparison.OrdinalIgnoreCase) &&
                             assetName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        shaUrl = dlUrl;
                    }
                }
            }

            string cleanTag = tag.TrimStart('v', 'V');
            bool newer = IsVersionNewer(cleanTag, currentVer);

            return new UpdateCheckResult
            {
                HasUpdate = newer,
                CurrentVersion = currentVer,
                LatestVersion = string.IsNullOrWhiteSpace(tag) ? $"v{currentVer}" : tag,
                ReleaseName = name,
                ReleaseNotes = body.Trim(),
                ReleaseUrl = htmlUrl,
                ExeDownloadUrl = exeUrl,
                Sha256FileUrl = shaUrl,
                ExeSizeBytes = exeSize,
                Message = newer
                    ? $"Yeni sürüm hazır: {tag} (Mevcut: v{currentVer})"
                    : $"Uygulama güncel (v{currentVer})."
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                HasUpdate = false,
                CurrentVersion = currentVer,
                LatestVersion = currentVer,
                Message = $"Sürüm kontrolü yapılamadı: {ex.Message}"
            };
        }
    }

    public async Task<(bool Ok, string StagedExePath, string Message)> DownloadAndVerifyUpdateAsync(
        UpdateCheckResult update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(update.ExeDownloadUrl))
            return (false, "", "Bu sürümde doğrudan indirilebilir KochanSentinel.exe bulunamadı.");

        string stageDir = Path.Combine(Path.GetTempPath(), "KochanSentinelUpdate");
        Directory.CreateDirectory(stageDir);
        string stagedPath = Path.Combine(stageDir, $"KochanSentinel_{SanitizeTag(update.LatestVersion)}.exe");

        try
        {
            if (File.Exists(stagedPath)) File.Delete(stagedPath);

            progress?.Report(new UpdateDownloadProgress { Percent = 2, StatusText = "Yeni sürüm indiriliyor..." });

            using var req = new HttpRequestMessage(HttpMethod.Get, update.ExeDownloadUrl);
            req.Headers.Add("User-Agent", $"KochanSentinel/{GetCurrentAppVersion()}");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, "", $"İndirme başarısız (HTTP {(int)resp.StatusCode}).");

            long totalBytes = resp.Content.Headers.ContentLength ?? update.ExeSizeBytes;
            long downloaded = 0;

            await using (var remoteStream = await resp.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long lastReport = Environment.TickCount64;
                int read;
                while ((read = await remoteStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;

                    long now = Environment.TickCount64;
                    if (now - lastReport >= 120 || downloaded == totalBytes)
                    {
                        lastReport = now;
                        double pct = totalBytes > 0 ? Math.Clamp(downloaded * 100.0 / totalBytes, 1, 95) : 50;
                        string mbDone = (downloaded / (1024.0 * 1024.0)).ToString("F1");
                        string mbTotal = totalBytes > 0 ? $" / {(totalBytes / (1024.0 * 1024.0)):F1} MB" : " MB";
                        progress?.Report(new UpdateDownloadProgress
                        {
                            Percent = pct,
                            BytesDownloaded = downloaded,
                            TotalBytes = totalBytes,
                            StatusText = $"İndiriliyor: {mbDone}{mbTotal} (%{pct:F0})"
                        });
                    }
                }
            }

            // Basic PE sanity check (MZ header)
            var fi = new FileInfo(stagedPath);
            if (!fi.Exists || fi.Length < 1024 * 100)
                return (false, "", "İndirilen dosya eksik veya geçersiz görünüyor.");

            byte[] mz = new byte[2];
            using (var checkFs = File.OpenRead(stagedPath))
                _ = checkFs.Read(mz, 0, 2);
            if (mz[0] != (byte)'M' || mz[1] != (byte)'Z')
                return (false, "", "İndirilen dosya geçerli bir Windows çalıştırılabilir (EXE) dosyası değil!");

            // SHA-256 verification against SHA256SUMS.txt (if published in Release)
            progress?.Report(new UpdateDownloadProgress { Percent = 97, StatusText = "SHA-256 bütünlük doğrulaması yapılıyor..." });
            string actualSha256;
            await using (var fsHash = File.OpenRead(stagedPath))
            {
                var hashBytes = await SHA256.HashDataAsync(fsHash, ct);
                actualSha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(update.Sha256FileUrl))
            {
                string? expectedHash = await FetchExpectedSha256Async(update.Sha256FileUrl, ct);
                if (!string.IsNullOrWhiteSpace(expectedHash) &&
                    !actualSha256.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(stagedPath); } catch { }
                    return (false, "", "GÜVENLİK ENGELİ: İndirilen EXE'nin SHA-256 özeti GitHub SHA256SUMS.txt ile eşleşmiyor! Güncelleme iptal edildi.");
                }
            }

            progress?.Report(new UpdateDownloadProgress { Percent = 100, StatusText = "Doğrulandı — uygulama yeniden başlatılıyor..." });
            return (true, stagedPath, $"Doğrulandı (SHA-256: {actualSha256[..12]}...).");
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
            return (false, "", "Güncelleme indirmesi iptal edildi.");
        }
        catch (Exception ex)
        {
            try { if (File.Exists(stagedPath)) File.Delete(stagedPath); } catch { }
            return (false, "", $"Güncelleme hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// Performs atomic self-replacement of the currently running single-file EXE on Windows:
    /// 1) Renames running EXE -> EXE.old (permitted on NTFS while running!)
    /// 2) Moves verified new EXE into the original path
    /// 3) Launches the new EXE with --updated flag.
    /// Falls back to launching staged EXE with --apply-update if direct swap is blocked.
    /// </summary>
    public static (bool Ok, string Message) ApplyStagedUpdateAndRelaunch(string stagedExePath, Action beforeLaunchShutdownPrep)
    {
        try
        {
            if (!File.Exists(stagedExePath))
                return (false, "İndirilen güncelleme dosyası bulunamadı.");

            string? currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                return (false, "Mevcut uygulama yolu alınamadı.");

            if (currentExe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var c = Path.ChangeExtension(currentExe, ".exe");
                if (File.Exists(c)) currentExe = c;
            }

            // Stop watchdog + mark clean exit + release single-instance mutex BEFORE launching replacement
            beforeLaunchShutdownPrep();

            string backupPath = currentExe + ".old";
            try
            {
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(currentExe, backupPath, overwrite: true);
                File.Copy(stagedExePath, currentExe, overwrite: true);
                try { File.Delete(stagedExePath); } catch { }

                Process.Start(new ProcessStartInfo(currentExe, "--updated")
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(currentExe) ?? ""
                });
                return (true, "Güncelleme uygulandı, yeni sürüm başlatılıyor...");
            }
            catch
            {
                // Rollback if move succeeded partially
                try
                {
                    if (!File.Exists(currentExe) && File.Exists(backupPath))
                        File.Move(backupPath, currentExe, overwrite: true);
                }
                catch { }

                // Fallback: let the staged EXE wait for our PID to exit, overwrite target, and relaunch
                Process.Start(new ProcessStartInfo(stagedExePath, $"--apply-update {Environment.ProcessId} \"{currentExe}\"")
                {
                    UseShellExecute = true
                });
                return (true, "Güncelleme yardımcısı başlatıldı...");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Güncelleme uygulanamadı: {ex.Message}");
        }
    }

    public static void CleanupOldUpdateLeftovers()
    {
        try
        {
            string? currentExe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(currentExe))
            {
                string oldFile = currentExe + ".old";
                if (File.Exists(oldFile))
                {
                    try { File.Delete(oldFile); } catch { }
                }
            }
            string stageDir = Path.Combine(Path.GetTempPath(), "KochanSentinelUpdate");
            if (Directory.Exists(stageDir))
            {
                foreach (var f in Directory.EnumerateFiles(stageDir, "*.exe"))
                {
                    if (!string.Equals(f, currentExe, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static async Task<string?> FetchExpectedSha256Async(string shaUrl, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, shaUrl);
            req.Headers.Add("User-Agent", $"KochanSentinel/{GetCurrentAppVersion()}");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            string text = await resp.Content.ReadAsStringAsync(ct);
            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Contains("KochanSentinel.exe", StringComparison.OrdinalIgnoreCase) || line.Length >= 64)
                {
                    var token = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
                    if (token.Length == 64 && IsHexString(token))
                        return token.ToLowerInvariant();
                }
            }
        }
        catch { }
        return null;
    }

    private static bool IsHexString(string s)
    {
        foreach (char c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }

    private static bool IsVersionNewer(string remote, string current)
    {
        if (!Version.TryParse(NormalizeVersion(remote), out var rVer)) return false;
        if (!Version.TryParse(NormalizeVersion(current), out var cVer)) return false;
        return rVer > cVer;
    }

    private static string NormalizeVersion(string v)
    {
        var clean = v.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];
        var parts = clean.Split('.');
        if (parts.Length == 2) return $"{clean}.0";
        return clean;
    }

    private static string SanitizeTag(string tag)
    {
        var chars = Array.FindAll(tag.ToCharArray(), c => char.IsLetterOrDigit(c) || c == '.' || c == '-');
        return chars.Length > 0 ? new string(chars) : "latest";
    }
}
