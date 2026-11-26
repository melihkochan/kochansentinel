using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KochanSentinel.Core.Models;

namespace KochanSentinel.App.Converters;

public sealed class ExePathToIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        return Cache.GetOrAdd(path, p =>
        {
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(p);
                if (icon == null) return null;
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                return null;
            }
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBrush = Freeze(System.Windows.Media.Color.FromRgb(252, 165, 165));
    private static readonly SolidColorBrush HighBrush = Freeze(System.Windows.Media.Color.FromRgb(253, 186, 116));
    private static readonly SolidColorBrush MediumBrush = Freeze(System.Windows.Media.Color.FromRgb(253, 224, 71));
    private static readonly SolidColorBrush LowBrush = Freeze(System.Windows.Media.Color.FromRgb(147, 197, 253));
    private static readonly SolidColorBrush InfoBrush = Freeze(System.Windows.Media.Color.FromRgb(134, 239, 172));
    private static readonly SolidColorBrush FallbackBrush = Freeze(Colors.Gray);

    internal static SolidColorBrush Freeze(System.Windows.Media.Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => CriticalBrush,
                FindingSeverity.High => HighBrush,
                FindingSeverity.Medium => MediumBrush,
                FindingSeverity.Low => LowBrush,
                _ => InfoBrush
            };
        }
        return FallbackBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SeverityToBgBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBg = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(70, 239, 68, 68));
    private static readonly SolidColorBrush HighBg = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(65, 249, 115, 22));
    private static readonly SolidColorBrush MediumBg = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(55, 234, 179, 8));
    private static readonly SolidColorBrush LowBg = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(55, 59, 130, 246));
    private static readonly SolidColorBrush InfoBg = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(55, 34, 197, 94));
    private static readonly SolidColorBrush TransparentBg = SeverityToBrushConverter.Freeze(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => CriticalBg,
                FindingSeverity.High => HighBg,
                FindingSeverity.Medium => MediumBg,
                FindingSeverity.Low => LowBg,
                _ => InfoBg
            };
        }
        return TransparentBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SeverityToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBd = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(140, 239, 68, 68));
    private static readonly SolidColorBrush HighBd = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(130, 249, 115, 22));
    private static readonly SolidColorBrush MediumBd = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(120, 234, 179, 8));
    private static readonly SolidColorBrush LowBd = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(120, 59, 130, 246));
    private static readonly SolidColorBrush InfoBd = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromArgb(120, 34, 197, 94));
    private static readonly SolidColorBrush TransparentBd = SeverityToBrushConverter.Freeze(Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => CriticalBd,
                FindingSeverity.High => HighBd,
                FindingSeverity.Medium => MediumBd,
                FindingSeverity.Low => LowBd,
                _ => InfoBd
            };
        }
        return TransparentBd;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class PercentToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush HotBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush WarnBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush CoolBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(56, 189, 248));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct >= 85) return HotBrush;
        if (pct >= 65) return WarnBrush;
        return CoolBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s)
            return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
        return value != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, System.Windows.Media.Brush> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SolidColorBrush DefaultBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(56, 189, 248));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            return Cache.GetOrAdd(hex, h =>
            {
                try
                {
                    if (new System.Windows.Media.BrushConverter().ConvertFromString(h) is System.Windows.Media.Brush b)
                    {
                        if (b.CanFreeze) b.Freeze();
                        return b;
                    }
                }
                catch { }
                return DefaultBrush;
            });
        }
        return DefaultBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush KochanBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(56, 189, 248));
    private static readonly SolidColorBrush BrowserBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(14, 165, 233));
    private static readonly SolidColorBrush GameBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(168, 85, 247));
    private static readonly SolidColorBrush AppBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(16, 185, 129));
    private static readonly SolidColorBrush OtherBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(100, 116, 139));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cat = value?.ToString() ?? "";
        return cat switch
        {
            "KOCHAN" => KochanBrush,
            "Tarayıcı" => BrowserBrush,
            "Oyun / Launcher" => GameBrush,
            "Uygulama" => AppBrush,
            _ => OtherBrush
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SignedToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush SignedBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush UnsignedBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(249, 115, 22));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? SignedBrush : UnsignedBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SignedToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "✓ İmzalı" : "⚠ İmzasız";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class HealthScoreToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(34, 197, 94));
    private static readonly SolidColorBrush AmberBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush RedBrush = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int score = value is int i ? i : 100;
        if (score >= 85) return GreenBrush;
        if (score >= 65) return AmberBrush;
        return RedBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class DiskPercentToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush HotRed = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(239, 68, 68));
    private static readonly SolidColorBrush Amber = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(245, 158, 11));
    private static readonly SolidColorBrush SkyBlue = SeverityToBrushConverter.Freeze(System.Windows.Media.Color.FromRgb(56, 189, 248));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct >= 35) return HotRed;
        if (pct >= 15) return Amber;
        return SkyBlue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
