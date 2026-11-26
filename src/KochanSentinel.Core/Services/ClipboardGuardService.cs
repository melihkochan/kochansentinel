using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Crypto Clipper Malware Guard: inspects clipboard text updates (via Win32 WM_CLIPBOARDUPDATE).
/// If a copied crypto wallet address (BTC / ETH / TRON / SOL) is silently hijacked and replaced
/// with a different address within 4 seconds, flags the offending process and signals immediate restore.
/// </summary>
public sealed class ClipboardGuardService
{
    private static readonly Regex BtcRegex = new(@"^(bc1[a-zA-HJ-NP-Z0-9]{25,62}|[13][a-km-zA-HJ-NP-Z1-9]{25,34})$", RegexOptions.Compiled);
    private static readonly Regex EthRegex = new(@"^0x[a-fA-F0-9]{40}$", RegexOptions.Compiled);
    private static readonly Regex TronRegex = new(@"^T[A-Za-z1-9]{33}$", RegexOptions.Compiled);

    private string _lastWalletText = "";
    private string _lastCoinType = "";
    private DateTime _lastWalletUtc = DateTime.MinValue;
    private uint _lastOwnerPid;
    private DateTime _suppressUntilUtc = DateTime.MinValue;

    public bool IsEnabled { get; set; } = true;

    public (bool HijackDetected, string RestoreAddress, SecurityEvent? AlertEvent) InspectClipboardText(string? text)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(text) || DateTime.UtcNow < _suppressUntilUtc)
            return (false, "", null);

        string trimmed = text.Trim();
        if (trimmed.Length < 26 || trimmed.Length > 70 || trimmed.Contains(' ') || trimmed.Contains('\n'))
            return (false, "", null);

        string? coin = DetectWalletCoin(trimmed);
        if (coin == null) return (false, "", null);

        uint ownerPid = GetClipboardOwnerPid();
        var now = DateTime.UtcNow;

        // Classic Clipper Behavior: another process silently swaps the copied wallet within 4 seconds
        if (!string.IsNullOrEmpty(_lastWalletText) &&
            _lastCoinType == coin &&
            !_lastWalletText.Equals(trimmed, StringComparison.Ordinal) &&
            (now - _lastWalletUtc).TotalSeconds <= 4.0 &&
            ownerPid != _lastOwnerPid)
        {
            string suspectInfo = DescribeAndSuspendIfUnsigned(ownerPid);
            string safeOriginal = _lastWalletText;
            _suppressUntilUtc = now.AddSeconds(2);

            var ev = new SecurityEvent
            {
                Source = "KRİPTO CLIPPER KALKANI",
                Message = $"Pano kaçırma engellendi! {coin} cüzdan adresiniz değiştirilmeye çalışıldı ({trimmed[..10]}... → orijinal geri yüklendi). {suspectInfo}",
                Severity = FindingSeverity.Critical
            };
            return (true, safeOriginal, ev);
        }

        _lastWalletText = trimmed;
        _lastCoinType = coin;
        _lastWalletUtc = now;
        _lastOwnerPid = ownerPid;
        return (false, "", null);
    }

    private static string? DetectWalletCoin(string s)
    {
        if (EthRegex.IsMatch(s)) return "ETH/EVM";
        if (BtcRegex.IsMatch(s)) return "BTC";
        if (TronRegex.IsMatch(s)) return "TRON/USDT";
        return null;
    }

    private static uint GetClipboardOwnerPid()
    {
        try
        {
            IntPtr owner = NativeMethods.GetClipboardOwner();
            if (owner != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(owner, out uint pid);
                return pid;
            }
        }
        catch { }
        return 0;
    }

    private static string DescribeAndSuspendIfUnsigned(uint pid)
    {
        if (pid <= 4 || pid == (uint)Environment.ProcessId)
            return "Şüpheli süreç arka planda gizlenmiş.";

        try
        {
            using var p = Process.GetProcessById((int)pid);
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { }

            if (!string.IsNullOrEmpty(path) &&
                !path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase) &&
                !NativeMethods.VerifyFileOrCatalogSignature(path))
            {
                if (NativeMethods.NtSuspendProcess(p.Handle) == 0)
                    return $"İmzasız süreç donduruldu: {p.ProcessName} (PID {pid}).";
            }
            return $"Pano sahibi: {p.ProcessName} (PID {pid}).";
        }
        catch
        {
            return $"Şüpheli PID: {pid}.";
        }
    }
}
