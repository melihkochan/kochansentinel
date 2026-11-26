using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Real-time ETW monitor listening to Microsoft-Windows-Kernel-Network to measure
/// per-process download/upload throughput (KB/s) without packet capture drivers.
/// </summary>
public sealed class ProcessNetworkMonitorService : IDisposable
{
    private const string SessionName = "KochanSentinelNetTrace";
    private static readonly Guid KernelNetworkProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;

    private sealed class PidNetCounters
    {
        public long RxBytes;
        public long TxBytes;
    }

    private readonly ConcurrentDictionary<int, PidNetCounters> _accumulators = new();
    private DateTime _lastSnapshotUtc = DateTime.UtcNow;
    private Thread? _traceThread;
    private ulong _sessionHandle;
    private ulong _traceHandle = ulong.MaxValue;
    private IntPtr _propsPtr = IntPtr.Zero;
    private IntPtr _loggerNamePtr = IntPtr.Zero;
    private EventRecordCallbackDelegate? _callbackKeepAlive;
    private volatile bool _running;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallbackDelegate(IntPtr eventRecord);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME { public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public char[] StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public char[] DaylightName;
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _lastSnapshotUtc = DateTime.UtcNow;
        _traceThread = new Thread(RunTraceLoop)
        {
            IsBackground = true,
            Name = "KochanEtwNetMonitor"
        };
        _traceThread.Start();
    }

    private void RunTraceLoop()
    {
        try
        {
            int propsSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
            int totalSize = propsSize + 512;
            _propsPtr = Marshal.AllocHGlobal(totalSize);
            ZeroMemory(_propsPtr, totalSize);

            var props = new EVENT_TRACE_PROPERTIES
            {
                Wnode = new WNODE_HEADER { BufferSize = (uint)totalSize, ClientContext = 1, Flags = WNODE_FLAG_TRACED_GUID },
                BufferSize = 64,
                MinimumBuffers = 2,
                MaximumBuffers = 16,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,
                LoggerNameOffset = (uint)propsSize
            };
            Marshal.StructureToPtr(props, _propsPtr, false);

            ControlTraceW(0, SessionName, _propsPtr, EVENT_TRACE_CONTROL_STOP);
            ZeroMemory(_propsPtr, totalSize);
            Marshal.StructureToPtr(props, _propsPtr, false);

            if (StartTraceW(out _sessionHandle, SessionName, _propsPtr) != 0)
            {
                _running = false;
                return;
            }

            var prov = KernelNetworkProviderGuid;
            // Keyword 0x10 = KERNEL_NETWORK_KEYWORD_IPV4, 0x20 = KERNEL_NETWORK_KEYWORD_IPV6
            EnableTraceEx2(_sessionHandle, ref prov, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0x30, 0, 0, IntPtr.Zero);

            _loggerNamePtr = Marshal.StringToHGlobalUni(SessionName);
            _callbackKeepAlive = OnEventRecord;

            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = _loggerNamePtr,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callbackKeepAlive),
                LogfileHeader = new TRACE_LOGFILE_HEADER
                {
                    TimeZone = new TIME_ZONE_INFORMATION { StandardName = new char[32], DaylightName = new char[32] }
                }
            };

            _traceHandle = OpenTraceW(ref logfile);
            if (_traceHandle == ulong.MaxValue)
            {
                StopSession();
                return;
            }

            ProcessTrace(new[] { _traceHandle }, 1, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            _running = false;
        }
    }

    private void OnEventRecord(IntPtr recordPtr)
    {
        if (!_running || recordPtr == IntPtr.Zero) return;
        try
        {
            // EVENT_RECORD offsets on x64 Windows:
            // 0x28: EventHeader.EventDescriptor.Id (USHORT)
            // 0x56: UserDataLength (USHORT)
            // 0x60: UserData (PVOID)
            ushort eventId = (ushort)Marshal.ReadInt16(recordPtr, 0x28);
            bool isSend = eventId is 10 or 26 or 42 or 58;
            bool isRecv = eventId is 11 or 27 or 43 or 59;
            if (!isSend && !isRecv) return;

            ushort userDataLen = (ushort)Marshal.ReadInt16(recordPtr, 0x56);
            if (userDataLen < 8) return;

            IntPtr userData = Marshal.ReadIntPtr(recordPtr, 0x60);
            if (userData == IntPtr.Zero) return;

            int pid = Marshal.ReadInt32(userData, 0);
            int size = Marshal.ReadInt32(userData, 4);
            if (pid <= 4 || size <= 0 || size > 10_000_000) return;

            var counter = _accumulators.GetOrAdd(pid, _ => new PidNetCounters());
            if (isRecv)
                Interlocked.Add(ref counter.RxBytes, size);
            else
                Interlocked.Add(ref counter.TxBytes, size);
        }
        catch { }
    }

    /// <summary>
    /// Drains accumulated bytes since last call and returns (DownloadKbPerSec, UploadKbPerSec) per PID.
    /// </summary>
    public Dictionary<int, (double DownloadKbPerSec, double UploadKbPerSec)> CaptureRatesAndReset()
    {
        var result = new Dictionary<int, (double, double)>();
        var now = DateTime.UtcNow;
        double elapsedSec = Math.Max(0.25, (now - _lastSnapshotUtc).TotalSeconds);
        _lastSnapshotUtc = now;

        foreach (var kv in _accumulators)
        {
            long rx = Interlocked.Exchange(ref kv.Value.RxBytes, 0);
            long tx = Interlocked.Exchange(ref kv.Value.TxBytes, 0);
            if (rx > 0 || tx > 0)
            {
                result[kv.Key] = (
                    Math.Round(rx / 1024.0 / elapsedSec, 1),
                    Math.Round(tx / 1024.0 / elapsedSec, 1)
                );
            }
        }

        if (_accumulators.Count > 500)
        {
            foreach (var key in _accumulators.Keys)
            {
                if (!result.ContainsKey(key))
                    _accumulators.TryRemove(key, out _);
            }
        }

        return result;
    }

    private static void ZeroMemory(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i++) Marshal.WriteByte(ptr, i, 0);
    }

    private void StopSession()
    {
        if (_sessionHandle != 0 && _propsPtr != IntPtr.Zero)
        {
            try { ControlTraceW(_sessionHandle, null, _propsPtr, EVENT_TRACE_CONTROL_STOP); } catch { }
            _sessionHandle = 0;
        }
    }

    public void Dispose()
    {
        _running = false;
        StopSession();
        if (_traceHandle != ulong.MaxValue)
        {
            try { CloseTrace(_traceHandle); } catch { }
            _traceHandle = ulong.MaxValue;
        }
        try
        {
            if (_traceThread != null && _traceThread.IsAlive && Thread.CurrentThread != _traceThread)
                _traceThread.Join(1200);
        }
        catch { }

        if (_propsPtr != IntPtr.Zero) { Marshal.FreeHGlobal(_propsPtr); _propsPtr = IntPtr.Zero; }
        if (_loggerNamePtr != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerNamePtr); _loggerNamePtr = IntPtr.Zero; }
    }
}
