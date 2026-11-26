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
/// - Live Hardware Brightness &amp; Contrast via DDC/CI (dxva2.dll) or WMI (laptops).
/// - Instant reversible Color &amp; Gamma Ramp presets (FPS Clarity, sRGB, Night Eye-Care, Cinema, Default).
/// </summary>
public sealed class DisplayAndColorService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CDS_UPDATEREGISTRY = 0x00000001;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DISPLAY_DEVICE_ACTIVE = 0x00000001;
    private const int DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004;

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
        new() { Id = "default", Name = "Varsayılan (Standart 6500K)", Description = "Windows standart 2.2 gama ve nötr beyaz dengesi — tüm renk değişikliklerini sıfırlar.", Gamma = 1.0, RedScale = 1.0, GreenScale = 1.0, BlueScale = 1.0, BrightnessLift = 0.0 },
        new() { Id = "fps_clarity", Name = "FPS / Rekabetçi Oyun Netliği", Description = "Karanlık gölgeleri hafifçe açar ve kontrast keskinliğini artırarak rakipleri seçmeyi kolaylaştırır.", Gamma = 1.16, RedScale = 1.0, GreenScale = 1.0, BlueScale = 0.98, BrightnessLift = 0.04 },
        new() { Id = "srgb_design", Name = "Tasarım / Dengeli sRGB", Description = "Aşırı soğuk/mavi panel sapmasını hafifçe nötrleştirerek daha doğal cilt ve gri tonları sunar.", Gamma = 0.98, RedScale = 1.0, GreenScale = 0.98, BlueScale = 0.94, BrightnessLift = 0.0 },
        new() { Id = "cinema_deep", Name = "Sinema / Derin Siyahlar", Description = "Gama eğrisini sinematik 2.35 seviyesine yaklaştırarak film ve hikayeli oyunlarda siyah derinliğini artırır.", Gamma = 0.90, RedScale = 1.0, GreenScale = 0.99, BlueScale = 0.96, BrightnessLift = -0.01 },
        new() { Id = "night_care", Name = "Gece / Göz Koruma (4500K)", Description = "Mavi ışık spektrumunu %28 azaltır; gece uzun süre bilgisayar başında göz yorgunluğunu düşürür.", Gamma = 1.02, RedScale = 1.0, GreenScale = 0.89, BlueScale = 0.72, BrightnessLift = 0.0 }
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

            // Find monitor friendly name
            string friendly = adapter.DeviceString;
            var monDev = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
            if (EnumDisplayDevicesW(adapter.DeviceName, 0, ref monDev, 0) && !string.IsNullOrWhiteSpace(monDev.DeviceString))
                friendly = monDev.DeviceString;
            if (wmiNames.Count > results.Count && !string.IsNullOrWhiteSpace(wmiNames[results.Count]))
                friendly = wmiNames[results.Count];

            // Scan all supported modes to find native max resolution & max Hz at current/native resolution
            int maxW = currentMode.dmPelsWidth;
            int maxH = currentMode.dmPelsHeight;
            int maxHzAtCurrent = currentMode.dmDisplayFrequency;
            int maxHzOverall = currentMode.dmDisplayFrequency;

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
                if (probe.dmDisplayFrequency > maxHzOverall)
                    maxHzOverall = probe.dmDisplayFrequency;
            }

            string iccName = ReadIccProfileName(adapter.DeviceName);
            int curBright = -1, curContrast = -1;
            bool supportsDdc = false;

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
            info.Recommendations.Add($"Çözünürlük doğal panel çözünürlüğünün ({info.MaxWidth}x{info.MaxHeight}) altında — bulanıklık olmaması için doğal çözünürlük önerilir.");
        }

        if (info.IccProfileName.Contains("Profil Yok", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("Özel ICC renk profili atanmamış. Doğru beyaz dengesi için aşağıdan sRGB/Tasarım ön ayarını deneyebilir veya Windows Renk Kalibrasyonunu (dccw) çalıştırabilirsiniz.");
        }
        else
        {
            info.Recommendations.Add($"Aktif ICC profili: {info.IccProfileName}.");
        }

        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("NVIDIA GPU tespit edildi: NVIDIA Denetim Masası → Çözünürlük Değiştirme altında 'Çıkış dinamik aralığı: Tam (0-255)' ayarını açmanız siyahları ve kontrastı ciddi iyileştirir. Geniş gamut (neon/aşırı doygun) monitörlerde 'novideo_srgb' önerilir.");
        }
        else if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            info.Recommendations.Add("AMD GPU tespit edildi: Adrenalin → Ekran → 'Özel Renk' bölümünde Renk Sıcaklığı Kontrolünü kapatıp 'Tam RGB 4:4:4' piksel biçimini seçmeniz en doğru sRGB renklerini verir.");
        }

        if (info.CurrentBrightness > 88)
        {
            info.Recommendations.Add($"Monitör parlaklığı oldukça yüksek (%{info.CurrentBrightness}). Uzun kullanımda göz yorgunluğu ve kontrast kaybını önlemek için %55–75 aralığı önerilir.");
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

    public (bool Ok, string Message) ApplyColorPreset(MonitorDisplayInfo? monitor, ColorProfilePreset preset)
    {
        try
        {
            string device = monitor?.DeviceName ?? @"\\.\DISPLAY1";
            IntPtr hdc = CreateDCW(null, device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return (false, "Ekran renk bağlamı (HDC) açılamadı.");

            try
            {
                var ramp = new RAMP
                {
                    Red = new ushort[256],
                    Green = new ushort[256],
                    Blue = new ushort[256]
                };

                double invGamma = preset.Gamma > 0.2 ? (1.0 / preset.Gamma) : 1.0;
                for (int i = 0; i < 256; i++)
                {
                    double norm = i / 255.0;
                    double curved = i == 0 ? 0.0 : Math.Clamp(Math.Pow(norm, invGamma) + preset.BrightnessLift * (1.0 - norm) * norm * 4.0, 0.0, 1.0);
                    ramp.Red[i] = (ushort)Math.Clamp(Math.Round(curved * preset.RedScale * 65535.0), 0, 65535);
                    ramp.Green[i] = (ushort)Math.Clamp(Math.Round(curved * preset.GreenScale * 65535.0), 0, 65535);
                    ramp.Blue[i] = (ushort)Math.Clamp(Math.Round(curved * preset.BlueScale * 65535.0), 0, 65535);
                }

                bool ok = SetDeviceGammaRamp(hdc, ref ramp);
                return ok
                    ? (true, $"\"{preset.Name}\" renk profili {monitor?.FriendlyName ?? "ekrana"} anında uygulandı.")
                    : (false, "Ekran sürücüsü GDI Gama Rampasını kabul etmedi (HDR açıkken Windows gama kilitler).");
            }
            finally
            {
                DeleteDC(hdc);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Renk profili hatası: {ex.Message}");
        }
    }

    /// <summary>
    /// Smooth software Gamma LUT fallback for monitors where DDC/CI is disabled in OSD.
    /// brightnessPct: 10..100 (100 = full native brightness), contrastPct: 10..100 (50 = neutral).
    /// </summary>
    public (bool Ok, string Message) ApplySoftwareBrightnessContrast(
        MonitorDisplayInfo? monitor,
        int brightnessPct,
        int contrastPct,
        ColorProfilePreset? basePreset = null)
    {
        try
        {
            string device = monitor?.DeviceName ?? @"\\.\DISPLAY1";
            IntPtr hdc = CreateDCW(null, device, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return (false, "Ekran renk bağlamı (HDC) açılamadı.");

            try
            {
                var p = basePreset ?? GetColorPresets()[0];
                double bScale = Math.Clamp(brightnessPct / 100.0, 0.20, 1.0);
                // contrastPct: 50 -> 1.0 (neutral), 10 -> 0.65 (softer), 100 -> 1.38 (punchier)
                double cFactor = 1.0 + ((Math.Clamp(contrastPct, 10, 100) - 50.0) / 50.0) * 0.35;
                double invGamma = p.Gamma > 0.2 ? (1.0 / p.Gamma) : 1.0;

                var ramp = new RAMP
                {
                    Red = new ushort[256],
                    Green = new ushort[256],
                    Blue = new ushort[256]
                };

                for (int i = 0; i < 256; i++)
                {
                    double norm = i / 255.0;
                    double v = i == 0 ? 0.0 : Math.Pow(norm, invGamma) + p.BrightnessLift * (1.0 - norm) * norm * 4.0;
                    v = ((v - 0.5) * cFactor) + 0.5;
                    v = Math.Clamp(v, 0.0, 1.0) * bScale;

                    ramp.Red[i] = (ushort)Math.Clamp(Math.Round(v * p.RedScale * 65535.0), 0, 65535);
                    ramp.Green[i] = (ushort)Math.Clamp(Math.Round(v * p.GreenScale * 65535.0), 0, 65535);
                    ramp.Blue[i] = (ushort)Math.Clamp(Math.Round(v * p.BlueScale * 65535.0), 0, 65535);
                }

                bool ok = SetDeviceGammaRamp(hdc, ref ramp);
                return ok
                    ? (true, $"Yazılımsal Gama: Parlaklık %{brightnessPct}, Kontrast %{contrastPct} uygulandı.")
                    : (false, "Gama rampası uygulanamadı (Windows HDR açıkken sistem gama kontrolünü kilitler).");
            }
            finally
            {
                DeleteDC(hdc);
            }
        }
        catch (Exception ex)
        {
            return (false, $"Parlaklık/Kontrast hatası: {ex.Message}");
        }
    }

    public bool SetHardwareBrightness(MonitorDisplayInfo monitor, int percent)
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

        // Laptop WMI fallback
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
        try
        {
            Process.Start(new ProcessStartInfo(tool) { UseShellExecute = true });
        }
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
