using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using KochanSentinel.Core.Interop;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Real-time non-invasive ETW Present monitor listening to DXGI & D3D9 Present_Start events
/// to measure actual frame rate of the active foreground 3D/game window without DLL injection.
/// </summary>
public sealed class FpsMonitorService : IDisposable
{
    private const string SessionName = "KochanSentinelFpsTrace";
    private static readonly Guid DxgiProviderGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9ProviderGuid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;

    private readonly ConcurrentDictionary<uint, ConcurrentQueue<long>> _presentTicksByPid = new();
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
    private static extern uint EnableTraceEx2(
        ulong traceHandle,
        ref Guid providerId,
        uint controlCode,
        byte level,
        ulong matchAnyKeyword,
        ulong matchAllKeyword,
        uint timeout,
        IntPtr enableParameters);

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
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] StandardName;
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public char[] DaylightName;
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
        _traceThread = new Thread(RunTraceLoop)
        {
            IsBackground = true,
            Name = "KochanEtwFpsMonitor"
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
                Wnode = new WNODE_HEADER
                {
                    BufferSize = (uint)totalSize,
                    ClientContext = 1,
                    Flags = WNODE_FLAG_TRACED_GUID
                },
                BufferSize = 64,
                MinimumBuffers = 2,
                MaximumBuffers = 16,
                LogFileMode = EVENT_TRACE_REAL_TIME_MODE,
                FlushTimer = 1,
                LoggerNameOffset = (uint)propsSize
            };
            Marshal.StructureToPtr(props, _propsPtr, false);

            // Stop any orphaned trace session from an ungraceful exit
            ControlTraceW(0, SessionName, _propsPtr, EVENT_TRACE_CONTROL_STOP);

            // Re-zero & re-populate after ControlTraceW
            ZeroMemory(_propsPtr, totalSize);
            Marshal.StructureToPtr(props, _propsPtr, false);

            uint err = StartTraceW(out _sessionHandle, SessionName, _propsPtr);
            if (err != 0)
            {
                _running = false;
                return;
            }

            var dxgi = DxgiProviderGuid;
            var d3d9 = D3D9ProviderGuid;
            EnableTraceEx2(_sessionHandle, ref dxgi, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0xFFFFFFFFFFFFFFFF, 0, 0, IntPtr.Zero);
            EnableTraceEx2(_sessionHandle, ref d3d9, EVENT_CONTROL_CODE_ENABLE_PROVIDER, TRACE_LEVEL_INFORMATION, 0xFFFFFFFFFFFFFFFF, 0, 0, IntPtr.Zero);

            _loggerNamePtr = Marshal.StringToHGlobalUni(SessionName);
            _callbackKeepAlive = OnEventRecord;

            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = _loggerNamePtr,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callbackKeepAlive),
                LogfileHeader = new TRACE_LOGFILE_HEADER
                {
                    TimeZone = new TIME_ZONE_INFORMATION
                    {
                        StandardName = new char[32],
                        DaylightName = new char[32]
                    }
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
            // EVENT_HEADER offsets (evntcons.h):
            // 0x0C: ULONG ProcessId
            // 0x18: GUID ProviderId (16 bytes)
            // 0x28: USHORT EventDescriptor.Id
            uint pid = (uint)Marshal.ReadInt32(recordPtr, 0x0C);
            if (pid <= 4 || pid == (uint)Environment.ProcessId) return;

            ushort eventId = (ushort)Marshal.ReadInt16(recordPtr, 0x28);
            Guid provider = ReadGuid(recordPtr, 0x18);

            bool isPresent =
                (provider == DxgiProviderGuid && eventId == 42) ||
                (provider == D3D9ProviderGuid && eventId == 1);

            if (!isPresent) return;

            long nowTicks = DateTime.UtcNow.Ticks;
            var queue = _presentTicksByPid.GetOrAdd(pid, _ => new ConcurrentQueue<long>());
            queue.Enqueue(nowTicks);

            long cutoff = nowTicks - TimeSpan.TicksPerSecond * 2;
            while (queue.TryPeek(out long oldest) && oldest < cutoff)
                queue.TryDequeue(out _);
        }
        catch { }
    }

    public int? GetForegroundFps()
    {
        if (!_running) return null;
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint fgPid);
            if (fgPid <= 4) return null;

            long nowTicks = DateTime.UtcNow.Ticks;
            long oneSecAgo = nowTicks - TimeSpan.TicksPerSecond;

            if (_presentTicksByPid.TryGetValue(fgPid, out var queue))
            {
                while (queue.TryPeek(out long oldest) && oldest < oneSecAgo - TimeSpan.TicksPerSecond)
                    queue.TryDequeue(out _);

                int count = 0;
                foreach (var ts in queue)
                {
                    if (ts >= oneSecAgo) count++;
                }
                return count >= 2 ? count : null;
            }
        }
        catch { }
        return null;
    }

    private static Guid ReadGuid(IntPtr ptr, int offset)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            bytes[i] = Marshal.ReadByte(ptr, offset + i);
        return new Guid(bytes);
    }

    private static void ZeroMemory(IntPtr ptr, int size)
    {
        for (int i = 0; i < size; i++)
            Marshal.WriteByte(ptr, i, 0);
    }

    private void StopSession()
    {
        if (_sessionHandle != 0 && _propsPtr != IntPtr.Zero)
        {
            try { ControlTraceW(_sessionHandle, null, _propsPtr, EVENT_TRACE_CONTROL_STOP); }
            catch { }
            _sessionHandle = 0;
        }
    }

    public void Dispose()
    {
        _running = false;
        StopSession();
        if (_traceHandle != ulong.MaxValue)
        {
            try { CloseTrace(_traceHandle); }
            catch { }
            _traceHandle = ulong.MaxValue;
        }
        if (_propsPtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_propsPtr);
            _propsPtr = IntPtr.Zero;
        }
        if (_loggerNamePtr != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_loggerNamePtr);
            _loggerNamePtr = IntPtr.Zero;
        }
    }
}
