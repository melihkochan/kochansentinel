using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class RamOptimizerService
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "smss", "csrss", "wininit", "services", "lsass", "svchost",
        "winlogon", "dwm", "explorer", "MsMpEng", "NisSrv", "KochanSentinel", "KochanSentinel.App"
    };

    private static readonly HashSet<string> EcoTargetBloatware = new(StringComparer.OrdinalIgnoreCase)
    {
        "OneDrive", "SearchIndexer", "AdobeUpdateService", "CCXProcess",
        "EpicWebHelper", "PhoneExperienceHost", "YourPhone", "Widgets", "GameBar", "Cortana"
    };

    public async Task<RamOptimizeResult> OptimizeAsync(bool purgeStandbyList = true)
    {
        return await Task.Run(() =>
        {
            var (beforeUsedGb, beforeAvailMb) = GetMemorySnapshot();
            int trimmed = 0;
            int ecoApplied = 0;
            bool standbyPurged = false;

            uint fgPid = 0;
            IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
            if (fgHwnd != IntPtr.Zero)
                NativeMethods.GetWindowThreadProcessId(fgHwnd, out fgPid);

            // 1. Safe WorkingSet trim + EcoQoS Efficiency Mode on background bloatware
            int currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id <= 4 || proc.Id == currentPid || proc.Id == fgPid || ProtectedProcesses.Contains(proc.ProcessName))
                        continue;

                    if (EcoTargetBloatware.Contains(proc.ProcessName))
                    {
                        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                            StateMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED
                        };
                        if (NativeMethods.SetProcessInformation(proc.Handle, NativeMethods.ProcessPowerThrottling, ref state, Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>()))
                            ecoApplied++;
                    }

                    if (proc.WorkingSet64 > 80 * 1024 * 1024)
                    {
                        if (NativeMethods.EmptyWorkingSet(proc.Handle))
                            trimmed++;
                    }
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }

            // 2. Purge Windows Standby Memory List (reduces micro-stutters in games)
            if (purgeStandbyList)
            {
                standbyPurged = TryPurgeStandbyList();
            }

            var (afterUsedGb, afterAvailMb) = GetMemorySnapshot();
            double freedMb = Math.Max(0, Math.Round(afterAvailMb - beforeAvailMb, 1));

            return new RamOptimizeResult
            {
                BeforeUsedGb = beforeUsedGb,
                AfterUsedGb = afterUsedGb,
                FreedMb = freedMb,
                TrimmedProcesses = trimmed,
                EcoModeAppliedCount = ecoApplied,
                StandbyListPurged = standbyPurged,
                Message = standbyPurged
                    ? $"{trimmed} işlem temizlendi, {ecoApplied} arka plan uygulaması Eco moduna alındı, Standby List boşaltıldı ({freedMb:F0} MB RAM açıldı)."
                    : $"{trimmed} işlem temizlendi, {ecoApplied} arka plan uygulaması Eco moduna alındı ({freedMb:F0} MB RAM açıldı)."
            };
        });
    }

    public static bool TryPurgeStandbyList()
    {
        try
        {
            NativeMethods.EnablePrivilege("SeProfileSingleProcessPrivilege");
            int command = NativeMethods.MemoryPurgeStandbyList;
            IntPtr ptr = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(ptr, command);
                int status = NativeMethods.NtSetSystemInformation(
                    NativeMethods.SystemMemoryListInformation,
                    ptr,
                    sizeof(int));
                return status == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            return false;
        }
    }

    private static (double UsedGb, double AvailMb) GetMemorySnapshot()
    {
        var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mem))
        {
            ulong used = mem.ullTotalPhys - mem.ullAvailPhys;
            return (
                Math.Round(used / (1024.0 * 1024.0 * 1024.0), 2),
                mem.ullAvailPhys / (1024.0 * 1024.0)
            );
        }
        return (0, 0);
    }

    public static (bool Triggered, long FreedStandbyMb) CheckAndRunAutoIslc(AppSettings settings)
    {
        if (!settings.EnableAutoIslc) return (false, 0);
        try
        {
            var mem = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            if (!NativeMethods.GlobalMemoryStatusEx(ref mem)) return (false, 0);

            long availMb = (long)(mem.ullAvailPhys / (1024 * 1024));
            if (availMb >= settings.IslcFreeRamThresholdMb)
                return (false, 0);

            using var cNormal = new PerformanceCounter("Memory", "Standby Cache Normal Priority Bytes", true);
            using var cReserve = new PerformanceCounter("Memory", "Standby Cache Reserve Bytes", true);
            long standbyMb = (long)((cNormal.NextValue() + cReserve.NextValue()) / (1024 * 1024));

            if (standbyMb >= settings.IslcStandbyThresholdMb)
            {
                if (TryPurgeStandbyList())
                    return (true, standbyMb);
            }
        }
        catch { }
        return (false, 0);
    }
}
