using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Full Monitor &amp; Color Calibration Service:
/// - Enumerates active monitors, resolutions, current vs max refresh rate (Hz), bit depth, and ICC profiles.
/// - One-click Max Refresh Rate upgrade (ChangeDisplaySettingsEx).
/// - Real-time DWM GPU Color Matrix (Magnification.dll) that works reliably on Windows 10 &amp; Windows 11 24H2
///   even when GDI SetDeviceGammaRamp is ignored or DDC/CI is disabled/ignored by monitor firmware.
/// - Optional Hardware Brightness &amp; Contrast via DDC/CI (dxva2.dll) or WMI (laptops).
/// </summary>
public sealed class DisplayAndColorService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISPLAY_DEVICE_ACTIVE = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

    private bool _magInitialized;
    private bool _gdiResetOnce;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] transform;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsExW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string lpszDeviceName, ref DEVMODEW lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDCW(string? pwszDriver, string pwszDevice, string? pszPort, IntPtr pdm);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll", SetLastError = true)]
    private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfileW(IntPtr hDC, ref uint lpcbName, StringBuilder? lpszFilename);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorContrast(IntPtr hMonitor, out uint pdwMinimumContrast, out uint pdwCurrentContrast, out uint pdwMaximumContrast);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorContrast(IntPtr hMonitor, uint dwNewContrast);

    public static List<ColorProfilePreset> GetColorPresets() => new()
    {
        new()
        {
            Id = "default",
            Name = "Varsayılan (Standart 6500K)",
            BadgeText = "6500K Nötr",
            Description = "Tüm renk, canlılık ve kontrast efektlerini sıfırlayarak panelin doğal görüntüsüne döner.",
            Swatch1 = "#F4F4F5", Swatch2 = "#A1A1AA", Swatch3 = "#52525B",
            Gamma = 1.0, Contrast = 1.0, Saturation = 1.0, RedScale = 1.0, GreenScale = 1.0, BlueScale = 1.0, BrightnessLift = 0.0,
            IsActive = true
        },
        new()
        {
            Id = "fps_clarity",
            Name = "FPS / Rekabetçi Oyun (Digital Vibrance)",
            BadgeText = "Vibrance +32%",
            Description = "Renk canlılığını (+%32) ve karanlık gölge detaylarını artırarak FPS oyunlarında rakipleri seçmeyi kolaylaştırır.",
            Swatch1 = "#22C55E", Swatch2 = "#38BDF8", Swatch3 = "#FACC15",
            Gamma = 1.0, Contrast = 1.09, Saturation = 1.32, RedScale = 1.0, GreenScale = 1.0, BlueScale = 0.98, BrightnessLift = 0.035
        },
        new()
        {
            Id = "vivid_hdr",
            Name = "Canlı / Zengin Renkler",
            BadgeText = "Canlı +22%",
            Description = "Soluk IPS/VA panellere daha doygun renkler ve belirgin kontrast derinliği kazandırır.",
            Swatch1 = "#F43F5E", Swatch2 = "#A855F7", Swatch3 = "#3B82F6",
            Gamma = 1.0, Contrast = 1.12, Saturation = 1.22, RedScale = 1.0, GreenScale = 1.0, BlueScale = 1.0, BrightnessLift = 0.0
        },
        new()
        {
            Id = "srgb_design",
            Name = "Tasarım / Dengeli sRGB",
            BadgeText = "Doğal sRGB",
            Description = "Soğuk/mavi fabrika panel sapmasını nötrleştirerek daha doğal cilt ve nötr gri tonları sunar.",
            Swatch1 = "#F8FAFC", Swatch2 = "#FDE68A", Swatch3 = "#93C5FD",
            Gamma = 1.0, Contrast = 1.0, Saturation = 0.95, RedScale = 1.0, GreenScale = 0.96, BlueScale = 0.89, BrightnessLift = 0.0
        },
        new()
        {
            Id = "cinema_deep",
            Name = "Sinema / Derin Siyahlar",
            BadgeText = "Sinema 2.35",
            Description = "Siyah derinliğini ve sinematik kontrastı artırarak film ve hikayeli oyunlarda atmosferi güçlendirir.",
            Swatch1 = "#F97316", Swatch2 = "#9A3412", Swatch3 = "#27272A",
            Gamma = 1.0, Contrast = 1.16, Saturation = 1.06, RedScale = 1.0, GreenScale = 0.98, BlueScale = 0.93, BrightnessLift = -0.035
        },
        new()
        {
            Id = "night_care",
            Name = "Gece / Göz Koruma (4500K)",
            BadgeText = "Mavi Işık -%36",
            Description = "Mavi ışık spektrumunu %36 azaltıp sıcak amber ton uygular; gece kullanımında göz yorgunluğunu düşürür.",
            Swatch1 = "#F59E0B", Swatch2 = "#EA580C", Swatch3 = "#78350F",
            Gamma = 1.0, Contrast = 0.97, Saturation = 0.92, RedScale = 1.0, GreenScale = 0.84, BlueScale = 0.62, BrightnessLift = 0.0
        }
    };

    public List<MonitorDisplayInfo> InspectMonitors(string gpuName = "")
    {
        var results = new List<MonitorDisplayInfo>();
        var hmonByDevice = MapHMonitorsByDeviceName();
        var wmiNames = ReadWmiFriendlyMonitorNames();
        int? laptopBrightness = TryReadWmiLaptopBrightness();

        for (uint devIdx = 0; devIdx < 16; devIdx++)
        {
            var adapter = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (!EnumDisplayDevicesW(null, devIdx, ref adapter, 0)) break;
            if ((adapter.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0) continue;

            var currentMode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsExW(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref currentMode, 0))
                continue;

            string friendly = adapter.DeviceString;
            var monDev = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (EnumDisplayDevicesW(adapter.DeviceName, 0, ref monDev, 0) && !string.IsNullOrWhiteSpace(monDev.DeviceString))
                friendly = monDev.DeviceString;
            if (wmiNames.Count > results.Count && !string.IsNullOrWhiteSpace(wmiNames[results.Count]))
                friendly = wmiNames[results.Count];

            int maxW = currentMode.dmPelsWidth;
            int maxH = currentMode.dmPelsHeight;
            int maxHzAtCurrent = currentMode.dmDisplayFrequency;

            var probe = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            for (int modeNum = 0; EnumDisplaySettingsExW(adapter.DeviceName, modeNum, ref probe, 0); modeNum++)
            {
                if ((long)probe.dmPelsWidth * probe.dmPelsHeight > (long)maxW * maxH)
                {
                    maxW = probe.dmPelsWidth;
                    maxH = probe.dmPelsHeight;
                }
                if (probe.dmPelsWidth == currentMode.dmPelsWidth && probe.dmPelsHeight == currentMode.dmPelsHeight &&
                    probe.dmDisplayFrequency > maxHzAtCurrent)
                {
                    maxHzAtCurrent = probe.dmDisplayFrequency;
                }
            }

            string iccName = ReadIccProfileName(adapter.DeviceName);
            int curBright = -1, curContrast = -1;
            bool supportsDdc = false;
            bool isLaptopPanel = false;

            if (hmonByDevice.TryGetValue(adapter.DeviceName, out IntPtr hMon))
            {
                if (TryReadDdcBrightnessContrast(hMon, out int b, out int c))
                {
                    supportsDdc = true;
                    curBright = b;
                    curContrast = c;
                }
            }
            if (!supportsDdc && laptopBrightness.HasValue && (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0)
            {
                supportsDdc = true;
                isLaptopPanel = true;
                curBright = laptopBrightness.Value;
            }

            var info = new MonitorDisplayInfo
            {
                DeviceName = adapter.DeviceName,
                FriendlyName = friendly,
                IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                Width = currentMode.dmPelsWidth,
                Height = currentMode.dmPelsHeight,
                MaxWidth = maxW,
                MaxHeight = maxH,
                CurrentRefreshHz = currentMode.dmDisplayFrequency,
                MaxRefreshHz = maxHzAtCurrent,
                BitsPerPixel = currentMode.dmBitsPerPel,
                IccProfileName = iccName,
                SupportsDdcBrightness = supportsDdc,
                IsLaptopInternalPanel = isLaptopPanel,
                CurrentBrightness = curBright,
                CurrentContrast = curContrast
            };

            BuildSmartRecommendations(info, gpuName);
            results.Add(info);
        }

        return results;
    }

    private static void BuildSmartRecommendations(MonitorDisplayInfo info, string gpuName)
    {
        if (info.NeedsRefreshRateBoost)
        {
            info.Recommendations.Add($"KRİTİK: Ekranınız {info.MaxRefreshHz} Hz destekliyor ama şu an {info.CurrentRefreshHz} Hz çalışıyor! Yukarıdaki butonla tek tıkla {info.MaxRefreshHz} Hz'e geçin.");
        }
        else
        {
            info.Recommendations.Add($"Yenileme hızı optimum ({info.CurrentRefreshHz} Hz).");
        }

        if (info.Width < info.MaxWidth || info.Height < info.MaxHeight)
        {
            info.Recommendations.Add($"Çözünürlük doğal panel çözünürlüğünün ({info.MaxWidth}x{info.MaxHeight}) altında — net görüntü için doğal çözünürlük önerilir.");
        }

        if (info.IccProfileName.Contains("Profil Yok", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("Özel ICC profili atanmamış. Sağdaki canlı DWM renk profillerini anında test edebilir veya Windows Renk Kalibrasyonunu (dccw) kullanabilirsin.");
        }
        else
        {
            info.Recommendations.Add($"Aktif ICC profili: {info.IccProfileName}.");
        }

        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("NVIDIA GPU: NVIDIA Denetim Masası → Çözünürlük Değiştirme altında 'Çıkış dinamik aralığı: Tam (0-255)' ayarını açmanız siyahları ve kontrastı belirgin iyileştirir.");
        }
        else if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("AMD GPU: Adrenalin → Ekran → 'Özel Renk' bölümünde 'Tam RGB 4:4:4' piksel biçimini seçmeniz en doğru renkleri verir.");
        }
    }

    public (bool Ok, string Message) SetMaxRefreshRate(MonitorDisplayInfo monitor)
    {
        try
        {
            var mode = new DEVMODEW { dmSize = (short)Marshal.SizeOf<DEVMODEW>() };
            if (!EnumDisplaySettingsExW(monitor.DeviceName, ENUM_CURRENT_SETTINGS, ref mode, 0))
                return (false, "Mevcut ekran modu okunamadı.");

            if (monitor.MaxRefreshHz <= mode.dmDisplayFrequency)
                return (true, $"{monitor.FriendlyName} zaten maksimum yenileme hızında ({mode.dmDisplayFrequency} Hz).");

            mode.dmDisplayFrequency = monitor.MaxRefreshHz;
            mode.dmFields = 0x00400000; // DM_DISPLAYFREQUENCY
            int res = ChangeDisplaySettingsExW(monitor.DeviceName, ref mode, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
            if (res == DISP_CHANGE_SUCCESSFUL)
                return (true, $"{monitor.FriendlyName} başarıyla {monitor.MaxRefreshHz} Hz'e yükseltildi!");

            return (false, $"Yenileme hızı değiştirilemedi (Windows Kod: {res}).");
        }
        catch (Exception ex)
        {
            return (false, $"Ekran hızı hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// MUST be called on the WPF UI (STA) thread.
    /// Applies real-time GPU DWM Fullscreen Color Matrix via Windows Magnification.dll.
    /// Works reliably on Windows 10 and Windows 11 24H2 even when GDI SetDeviceGammaRamp is ignored or DDC/CI is off.
    /// </summary>
    public (bool Ok, string EngineName, string Message) ApplyLiveDwmColorAndBrightness(
        MonitorDisplayInfo? monitor,
        int brightnessPct,
        int contrastPct,
        ColorProfilePreset preset)
    {
        try
        {
            if (!_gdiResetOnce)
            {
                _gdiResetOnce = true;
                TryResetGdiGammaRamp(monitor?.DeviceName ?? @"\\.\DISPLAY1");
            }

            if (!_magInitialized)
                _magInitialized = MagInitialize();

            if (_magInitialized)
            {
                var effect = BuildColorMatrix(brightnessPct, contrastPct, preset);
                if (MagSetFullscreenColorEffect(ref effect))
                {
                    bool isIdentity = IsNeutral(brightnessPct, contrastPct, preset);
                    string msg = isIdentity
                        ? "Ekran renkleri ve parlaklık standart (6500K / %100) değerine sıfırlandı."
                        : $"DWM GPU Motoru aktif • Profil: {preset.Name} | Parlaklık %{brightnessPct} | Kontrast %{contrastPct}";
                    return (true, "DWM GPU", msg);
                }
            }
        }
        catch { }

        // Fallback to GDI Gamma LUT if Magnification.dll failed
        var (ok, gdiMsg) = ApplyGdiGammaFallback(monitor, brightnessPct, contrastPct, preset);
        return (ok, "GDI Gama", gdiMsg);
    }

    /// <summary>
    /// Cleanly resets the fullscreen DWM color effect and uninitializes Magnification API on true app exit.
    /// Must be called on the WPF UI thread.
    /// </summary>
    public void CleanupColorEngine()
    {
        if (!_magInitialized) return;
        try
        {
            var identity = BuildIdentityMatrix();
            MagSetFullscreenColorEffect(ref identity);
            MagUninitialize();
        }
        catch { }
        finally
        {
            _magInitialized = false;
        }
    }

    private static bool IsNeutral(int brightnessPct, int contrastPct, ColorProfilePreset p)
    {
        return brightnessPct >= 99 &&
               Math.Abs(contrastPct - 50) <= 1 &&
               Math.Abs(p.Contrast - 1.0) < 0.005 &&
               Math.Abs(p.Saturation - 1.0) < 0.005 &&
               Math.Abs(p.RedScale - 1.0) < 0.005 &&
               Math.Abs(p.GreenScale - 1.0) < 0.005 &&
               Math.Abs(p.BlueScale - 1.0) < 0.005 &&
               Math.Abs(p.BrightnessLift) < 0.002;
    }

    private static MAGCOLOREFFECT BuildIdentityMatrix() => new()
    {
        transform = new float[]
        {
            1f, 0f, 0f, 0f, 0f,
            0f, 1f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f, 0f,
            0f, 0f, 0f, 1f, 0f,
            0f, 0f, 0f, 0f, 1f
        }
    };

    private static MAGCOLOREFFECT BuildColorMatrix(int brightnessPct, int contrastPct, ColorProfilePreset p)
    {
        if (IsNeutral(brightnessPct, contrastPct, p))
            return BuildIdentityMatrix();

        float b = (float)Math.Clamp(brightnessPct / 100.0, 0.25, 1.0);
        // Slider contrast: 50 -> 1.00, 10 -> 0.68, 100 -> 1.40
        float sliderC = 1f + (float)((Math.Clamp(contrastPct, 10, 100) - 50.0) / 50.0 * 0.40);
        float c = Math.Clamp(sliderC * (float)p.Contrast, 0.55f, 1.65f);
        float sat = (float)Math.Clamp(p.Saturation, 0.2, 2.0);
        float lift = (float)Math.Clamp(p.BrightnessLift, -0.15, 0.15);

        float rS = (float)Math.Clamp(p.RedScale, 0.4, 1.2);
        float gS = (float)Math.Clamp(p.GreenScale, 0.4, 1.2);
        float bS = (float)Math.Clamp(p.BlueScale, 0.4, 1.2);

        float inv = 1f - sat;
        float sr = inv * 0.2126f;
        float sg = inv * 0.7152f;
        float sb = inv * 0.0722f;

        float kR = c * b * rS;
        float kG = c * b * gS;
        float kB = c * b * bS;

        float tBase = 0.5f * (1f - c) + lift;
        float tR = tBase * b * rS;
        float tG = tBase * b * gS;
        float tB = tBase * b * bS;

        return new MAGCOLOREFFECT
        {
            transform = new float[]
            {
                (sr + sat) * kR, sr * kG,         sr * kB,         0f, 0f,
                sg * kR,         (sg + sat) * kG, sg * kB,         0f, 0f,
                sb * kR,         sb * kG,         (sb + sat) * kB, 0f, 0f,
                0f,              0f,              0f,              1f, 0f,
                tR,              tG,              tB,              0f, 1f
            }
        };
    }

    private static void TryResetGdiGammaRamp(string device)
    {
        try
        {
            IntPtr hdc = CreateDCW(null, device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) return;
            try
            {
                var ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };
                for (int i = 0; i < 256; i++)
                {
                    ushort val = (ushort)(i * 257);
                    ramp.Red[i] = val;
                    ramp.Green[i] = val;
                    ramp.Blue[i] = val;
                }
                SetDeviceGammaRamp(hdc, ref ramp);
            }
            finally { DeleteDC(hdc); }
        }
        catch { }
    }

    private static (bool Ok, string Message) ApplyGdiGammaFallback(
        MonitorDisplayInfo? monitor,
        int brightnessPct,
        int contrastPct,
        ColorProfilePreset p)
    {
        try
        {
            string device = monitor?.DeviceName ?? @"\\.\DISPLAY1";
            IntPtr hdc = CreateDCW(null, device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return (false, "Ekran renk bağlamı (HDC) açılamadı.");

            try
            {
                double bScale = Math.Clamp(brightnessPct / 100.0, 0.25, 1.0);
                double cFactor = 1.0 + ((Math.Clamp(contrastPct, 10, 100) - 50.0) / 50.0) * 0.35;
                var ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };

                for (int i = 0; i < 256; i++)
                {
                    double norm = i / 255.0;
                    double v = ((norm - 0.5) * cFactor * p.Contrast) + 0.5 + p.BrightnessLift;
                    v = Math.Clamp(v, 0.0, 1.0) * bScale;
                    ramp.Red[i] = (ushort)Math.Clamp(Math.Round(v * p.RedScale * 65535.0), 0, 65535);
                    ramp.Green[i] = (ushort)Math.Clamp(Math.Round(v * p.GreenScale * 65535.0), 0, 65535);
                    ramp.Blue[i] = (ushort)Math.Clamp(Math.Round(v * p.BlueScale * 65535.0), 0, 65535);
                }

                bool ok = SetDeviceGammaRamp(hdc, ref ramp);
                return ok
                    ? (true, $"GDI Gama: {p.Name} | Parlaklık %{brightnessPct} | Kontrast %{contrastPct}")
                    : (false, "Ekran renk motoru Windows tarafından engellendi.");
            }
            finally { DeleteDC(hdc); }
        }
        catch (Exception ex)
        {
            return (false, $"Renk/parlaklık hatası: {ex.Message}");
        }
    }

    public bool SetHardwareBrightness(MonitorDisplayInfo monitor, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        if (monitor.IsLaptopInternalPanel)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject m in searcher.Get())
                {
                    m.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)percent });
                    return true;
                }
            }
            catch { }
        }

        var map = MapHMonitorsByDeviceName();
        if (map.TryGetValue(monitor.DeviceName, out IntPtr hMon) &&
            GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
        {
            var arr = new PHYSICAL_MONITOR[count];
            if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
            {
                try
                {
                    if (GetMonitorBrightness(arr[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                    {
                        uint target = min + (uint)Math.Round((max - min) * (percent / 100.0));
                        if (SetMonitorBrightness(arr[0].hPhysicalMonitor, target))
                            return true;
                    }
                }
                finally { DestroyPhysicalMonitors(count, arr); }
            }
        }
        return false;
    }

    public bool SetHardwareContrast(MonitorDisplayInfo monitor, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        var map = MapHMonitorsByDeviceName();
        if (map.TryGetValue(monitor.DeviceName, out IntPtr hMon) &&
            GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) && count > 0)
        {
            var arr = new PHYSICAL_MONITOR[count];
            if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
            {
                try
                {
                    if (GetMonitorContrast(arr[0].hPhysicalMonitor, out uint min, out _, out uint max) && max > min)
                    {
                        uint target = min + (uint)Math.Round((max - min) * (percent / 100.0));
                        return SetMonitorContrast(arr[0].hPhysicalMonitor, target);
                    }
                }
                finally { DestroyPhysicalMonitors(count, arr); }
            }
        }
        return false;
    }

    public static void LaunchWindowsTool(string tool)
    {
        try { Process.Start(new ProcessStartInfo(tool) { UseShellExecute = true }); }
        catch { }
    }

    private static string ReadIccProfileName(string deviceName)
    {
        IntPtr hdc = CreateDCW(null, deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return "Varsayılan (Profil Yok)";
        try
        {
            uint len = 512;
            var sb = new StringBuilder((int)len);
            if (GetICMProfileW(hdc, ref len, sb) && sb.Length > 0)
                return Path.GetFileName(sb.ToString());
        }
        catch { }
        finally { DeleteDC(hdc); }
        return "Varsayılan (Profil Yok)";
    }

    private static Dictionary<string, IntPtr> MapHMonitorsByDeviceName()
    {
        var map = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var mi = new MONITORINFOEXW { cbSize = Marshal.SizeOf<MONITORINFOEXW>() };
                if (GetMonitorInfoW(hMon, ref mi) && !string.IsNullOrEmpty(mi.szDevice))
                    map[mi.szDevice] = hMon;
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return map;
    }

    private static bool TryReadDdcBrightnessContrast(IntPtr hMon, out int brightnessPct, out int contrastPct)
    {
        brightnessPct = -1;
        contrastPct = -1;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out uint count) || count == 0) return false;
            var arr = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, arr)) return false;
            try
            {
                bool okB = GetMonitorBrightness(arr[0].hPhysicalMonitor, out uint minB, out uint curB, out uint maxB);
                if (okB && maxB > minB)
                    brightnessPct = (int)Math.Round((curB - minB) * 100.0 / (maxB - minB));
                bool okC = GetMonitorContrast(arr[0].hPhysicalMonitor, out uint minC, out uint curC, out uint maxC);
                if (okC && maxC > minC)
                    contrastPct = (int)Math.Round((curC - minC) * 100.0 / (maxC - minC));
                return okB || okC;
            }
            finally { DestroyPhysicalMonitors(count, arr); }
        }
        catch { return false; }
    }

    private static int? TryReadWmiLaptopBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject m in s.Get())
                return Convert.ToInt32(m["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    private static List<string> ReadWmiFriendlyMonitorNames()
    {
        var names = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT UserFriendlyName FROM WmiMonitorID");
            foreach (ManagementObject m in s.Get())
            {
                if (m["UserFriendlyName"] is ushort[] arr)
                {
                    var chars = arr.TakeWhile(c => c != 0).Select(c => (char)c).ToArray();
                    if (chars.Length > 0) names.Add(new string(chars).Trim());
                }
            }
        }
        catch { }
        return names;
    }
}
