using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class GameModeService
{
    private static readonly Guid HighPerformanceGuid = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid BalancedGuid = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "valorant", "VALORANT-Win64-Shipping", "League of Legends", "GTA5",
        "eldenring", "Cyberpunk2077", "FortniteClient-Win64-Shipping", "r5apex",
        "TslGame", "Overwatch", "javaw", "Minecraft.Windows", "RocketLeague",
        "witcher3", "rdr2", "bf2042", "cod", "dota2", "RustClient", "SoTGame"
    };

    private static readonly HashSet<string> AntiCheatProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping", "FortniteClient-Win64-Shipping", "r5apex", "RustClient", "TslGame"
    };

    private Guid? _savedPowerPlan;
    private readonly List<int> _suspendedPids = new();
    private readonly Dictionary<int, ProcessPriorityClass> _proBalancedPids = new();
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime Time)> _pbPrevCpu = new();
    private int _boostedGamePid;
    private ProcessPriorityClass _previousGamePriority = ProcessPriorityClass.Normal;
    private bool _timerResolutionActive;

    private static readonly HashSet<string> ProBalanceProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "services", "lsass", "svchost",
        "winlogon", "dwm", "explorer", "MsMpEng", "KochanSentinel.App", "audiodg", "Discord"
    };

    public bool IsAutoModeEnabled { get; set; } = true;
    public bool IsBoostActive { get; private set; }
    public string ActiveGameName { get; private set; } = "";
    public event Action<string, FindingSeverity>? OnStatusChanged;

    public void Tick(AppSettings settings)
    {
        if (!IsAutoModeEnabled && !IsBoostActive)
            return;

        if (!IsAutoModeEnabled && IsBoostActive)
        {
            DeactivateBoost();
            return;
        }

        var detected = DetectRunningGame(settings.CustomGameExecutables);

        if (detected != null && !IsBoostActive)
        {
            ActivateBoost(detected.Value.Pid, detected.Value.Name, settings);
        }
        else if (detected == null && IsBoostActive)
        {
            // Verify boosted game actually exited
            if (!IsProcessAlive(_boostedGamePid))
            {
                DeactivateBoost();
            }
        }
        else if (IsBoostActive && settings.EnableProBalance)
        {
            RunProBalanceTick();
        }
    }

    public void ActivateManualBoost(AppSettings settings)
    {
        if (IsBoostActive) return;
        ActivateBoost(0, "Manuel Oyun Modu", settings);
    }

    public void DeactivateBoost()
    {
        if (!IsBoostActive) return;

        // 1. Restore power plan
        if (_savedPowerPlan.HasValue)
        {
            var guid = _savedPowerPlan.Value;
            NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref guid);
            _savedPowerPlan = null;
        }

        // 2. Resume suspended background processes
        foreach (var pid in _suspendedPids)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                NativeMethods.NtResumeProcess(proc.Handle);
            }
            catch { }
        }
        _suspendedPids.Clear();

        // 3. Restore game priority if still running
        if (_boostedGamePid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(_boostedGamePid);
                proc.PriorityClass = _previousGamePriority;
            }
            catch { }
        }

        // 4. Restore Timer Resolution & ProBalance throttled processes
        if (_timerResolutionActive)
        {
            NativeMethods.NtSetTimerResolution(5000, false, out _);
            _timerResolutionActive = false;
        }

        foreach (var kv in _proBalancedPids)
        {
            try
            {
                using var p = Process.GetProcessById(kv.Key);
                p.PriorityClass = kv.Value;
                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                NativeMethods.SetProcessInformation(p.Handle, NativeMethods.ProcessPowerThrottling, ref state, Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
            }
            catch { }
        }
        _proBalancedPids.Clear();
        _pbPrevCpu.Clear();

        var prevName = ActiveGameName;
        _boostedGamePid = 0;
        ActiveGameName = "";
        IsBoostActive = false;
        OnStatusChanged?.Invoke($"Oyun Modu kapatıldı ({prevName}). Sistem ayarları geri yüklendi.", FindingSeverity.Info);
    }

    private void ActivateBoost(int gamePid, string gameName, AppSettings settings)
    {
        // 1. Save & switch power plan to High Performance
        try
        {
            if (NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out IntPtr pGuid) == 0 && pGuid != IntPtr.Zero)
            {
                _savedPowerPlan = Marshal.PtrToStructure<Guid>(pGuid);
                NativeMethods.LocalFree(pGuid);
            }
            var highPerf = HighPerformanceGuid;
            if (NativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref highPerf) != 0)
            {
                // Fallback if High Performance is hidden
                _savedPowerPlan ??= BalancedGuid;
            }
        }
        catch { }

        // 2. Boost game process priority (skip anti-cheat protected binaries to avoid kicks)
        _boostedGamePid = gamePid;
        if (gamePid > 0 && !AntiCheatProtected.Contains(gameName))
        {
            try
            {
                using var gameProc = Process.GetProcessById(gamePid);
                _previousGamePriority = gameProc.PriorityClass;
                gameProc.PriorityClass = ProcessPriorityClass.High;
            }
            catch { }
        }

        // 3. Suspend non-essential bloatware processes listed in settings
        _suspendedPids.Clear();
        var suppressSet = new HashSet<string>(settings.GameModeSuppressList, StringComparer.OrdinalIgnoreCase);
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (suppressSet.Contains(proc.ProcessName) && proc.Id != gamePid)
                {
                    if (NativeMethods.NtSuspendProcess(proc.Handle) == 0)
                        _suspendedPids.Add(proc.Id);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // 4. Purge Standby List & Enable 0.5ms Timer Resolution for lower input lag
        RamOptimizerService.TryPurgeStandbyList();
        if (settings.EnableTimerResolution05Ms)
        {
            if (NativeMethods.NtSetTimerResolution(5000, true, out _) == 0)
                _timerResolutionActive = true;
        }

        IsBoostActive = true;
        ActiveGameName = gameName;
        var timerNote = _timerResolutionActive ? " | 0.5ms Timer" : "";
        OnStatusChanged?.Invoke(
            $"Oyun Modu AKTİF: {gameName} | Yüksek Performans{timerNote} + {_suspendedPids.Count} arka plan uyutuldu.",
            FindingSeverity.Info);
    }

    private void RunProBalanceTick()
    {
        var now = DateTime.UtcNow;
        int cores = Environment.ProcessorCount;
        int currentPid = Environment.ProcessId;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id <= 4 || proc.Id == currentPid || proc.Id == _boostedGamePid ||
                    ProBalanceProtected.Contains(proc.ProcessName) || KnownGames.Contains(proc.ProcessName))
                    continue;

                var totalCpu = proc.TotalProcessorTime;
                if (_pbPrevCpu.TryGetValue(proc.Id, out var prev))
                {
                    double ms = (now - prev.Time).TotalMilliseconds;
                    if (ms > 500)
                    {
                        double pct = (totalCpu - prev.Cpu).TotalMilliseconds / (ms * cores) * 100.0;
                        if (pct > 15.0 && !_proBalancedPids.ContainsKey(proc.Id))
                        {
                            _proBalancedPids[proc.Id] = proc.PriorityClass;
                            proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                            {
                                Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                                StateMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                            };
                            NativeMethods.SetProcessInformation(proc.Handle, NativeMethods.ProcessPowerThrottling, ref state, Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
                        }
                    }
                }
                _pbPrevCpu[proc.Id] = (totalCpu, now);
            }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    private static (int Pid, string Name)? DetectRunningGame(List<string> customGames)
    {
        var customSet = new HashSet<string>(
            customGames.Select(g => System.IO.Path.GetFileNameWithoutExtension(g)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (KnownGames.Contains(proc.ProcessName) || customSet.Contains(proc.ProcessName))
                {
                    return (proc.Id, proc.ProcessName);
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        // Fallback: check if foreground window is D3D Exclusive Fullscreen
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN)
            {
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid > 4)
                    {
                        using var p = Process.GetProcessById((int)pid);
                        if (!p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                            return (p.Id, p.ProcessName);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return true; // Manual mode
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
