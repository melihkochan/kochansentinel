using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Anti-Ransomware Honeypot: places hidden alphabetically-first canary files in Documents &amp; Desktop.
/// Any external rename/overwrite triggers immediate high-IO unsigned process interception and decoy restoration.
/// </summary>
public sealed class RansomwareCanaryService : IDisposable
{
    private static readonly string[] CanaryNames =
    {
        "_00_kochan_canary.docx",
        "_00_kochan_canary.pdf"
    };

    private static readonly byte[] CanaryPayload = Encoding.UTF8.GetBytes(
        "KOCHAN-SENTINEL-ANTI-RANSOMWARE-CANARY-FILE-DO-NOT-MODIFY-" +
        "This decoy file protects your Documents and Desktop against mass encryption.");

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<string> _canaryPaths = new();
    private volatile bool _suppressEvents;
    private DateTime _lastAlertUtc = DateTime.MinValue;

    public bool IsActive { get; private set; }
    public event Action<SecurityEvent>? OnRansomwareAlert;

    public void Start()
    {
        if (IsActive) return;
        _suppressEvents = true;
        try
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            };

            foreach (var dir in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var name in CanaryNames)
                {
                    var path = Path.Combine(dir, name);
                    EnsureCanaryFile(path);
                    _canaryPaths.Add(path);
                }

                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };
                watcher.Changed += OnCanaryTouched;
                watcher.Renamed += OnCanaryTouched;
                watcher.Deleted += OnCanaryTouched;
                _watchers.Add(watcher);
            }

            IsActive = true;
            OnRansomwareAlert?.Invoke(new SecurityEvent
            {
                Source = "Ransomware Honeypot",
                Message = "Belgeler & Masaüstü tuzak fidye kalkanı aktif.",
                Severity = FindingSeverity.Info
            });
        }
        catch { }
        finally
        {
            _suppressEvents = false;
        }
    }

    private static void EnsureCanaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllBytes(path, CanaryPayload);
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
        }
        catch { }
    }

    private void OnCanaryTouched(object sender, FileSystemEventArgs e)
    {
        if (_suppressEvents || !IsActive) return;
        var name = Path.GetFileName(e.FullPath);
        if (!CanaryNames.Any(c => name.StartsWith(Path.GetFileNameWithoutExtension(c), StringComparison.OrdinalIgnoreCase)))
            return;

        if ((DateTime.UtcNow - _lastAlertUtc).TotalSeconds < 4) return;
        _lastAlertUtc = DateTime.UtcNow;

        _ = Task.Run(() =>
        {
            var neutralized = new List<string>();
            // Identify suspicious unsigned processes performing heavy I/O right now
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || proc.Id == Environment.ProcessId) continue;
                    string pName = proc.ProcessName;
                    if (pName.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                        pName.Equals("MsMpEng", StringComparison.OrdinalIgnoreCase) ||
                        pName.Equals("SearchIndexer", StringComparison.OrdinalIgnoreCase) ||
                        pName.Equals("OneDrive", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string path = "";
                    try { path = proc.MainModule?.FileName ?? ""; } catch { }
                    if (string.IsNullOrEmpty(path) || path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool signed = NativeMethods.VerifyFileOrCatalogSignature(path);
                    if (!signed && NativeMethods.GetProcessIoCounters(proc.Handle, out var io) &&
                        io.WriteTransferCount > 2 * 1024 * 1024)
                    {
                        proc.Kill(entireProcessTree: true);
                        neutralized.Add($"{pName} (PID {proc.Id})");
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            // Restore intact decoy files cleanly
            _suppressEvents = true;
            try
            {
                foreach (var p in _canaryPaths)
                    EnsureCanaryFile(p);
            }
            finally
            {
                _suppressEvents = false;
            }

            string extra = neutralized.Count > 0
                ? $" Süreç durduruldu: {string.Join(", ", neutralized)}."
                : " Lütfen belgelerinize yazan şüpheli uygulamayı kontrol edin.";

            OnRansomwareAlert?.Invoke(new SecurityEvent
            {
                Source = "RANSOMWARE HONEYPOT",
                Message = $"Tuzak dosya kurcalandı ({e.ChangeType}: {name})!{extra}",
                Severity = FindingSeverity.Critical
            });
        });
    }

    public void Stop()
    {
        IsActive = false;
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
        }
        _watchers.Clear();

        _suppressEvents = true;
        foreach (var p in _canaryPaths)
        {
            try
            {
                if (File.Exists(p))
                {
                    File.SetAttributes(p, FileAttributes.Normal);
                    File.Delete(p);
                }
            }
            catch { }
        }
        _canaryPaths.Clear();
        _suppressEvents = false;
    }

    public void Dispose() => Stop();
}
