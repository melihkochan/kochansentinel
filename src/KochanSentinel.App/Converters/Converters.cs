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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => new SolidColorBrush(System.Windows.Media.Color.FromRgb(252, 165, 165)), // #FCA5A5
                FindingSeverity.High => new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 186, 116)),     // #FDBA74
                FindingSeverity.Medium => new SolidColorBrush(System.Windows.Media.Color.FromRgb(253, 224, 71)),    // #FDE047
                FindingSeverity.Low => new SolidColorBrush(System.Windows.Media.Color.FromRgb(147, 197, 253)),      // #93C5FD
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(134, 239, 172))                         // #86EFAC
            };
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SeverityToBgBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => new SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 239, 68, 68)),
                FindingSeverity.High => new SolidColorBrush(System.Windows.Media.Color.FromArgb(65, 249, 115, 22)),
                FindingSeverity.Medium => new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 234, 179, 8)),
                FindingSeverity.Low => new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 59, 130, 246)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromArgb(55, 34, 197, 94))
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SeverityToBorderBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is FindingSeverity sev)
        {
            return sev switch
            {
                FindingSeverity.Critical => new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 239, 68, 68)),
                FindingSeverity.High => new SolidColorBrush(System.Windows.Media.Color.FromArgb(130, 249, 115, 22)),
                FindingSeverity.Medium => new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 234, 179, 8)),
                FindingSeverity.Low => new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 59, 130, 246)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromArgb(120, 34, 197, 94))
            };
        }
        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class PercentToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct >= 85) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
        if (pct >= 65) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && !string.IsNullOrWhiteSpace(hex))
                return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        }
        catch { }
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var cat = value?.ToString() ?? "";
        return cat switch
        {
            "KOCHAN" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
            "Tarayıcı" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 165, 233)),
            "Oyun / Launcher" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(168, 85, 247)),
            "Uygulama" => new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
            _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class SignedToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(249, 115, 22));

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
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int score = value is int i ? i : 100;
        if (score >= 85) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));  // Green
        if (score >= 65) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Amber
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));                 // Red
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public sealed class DiskPercentToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double pct = value is double d ? d : 0;
        if (pct >= 35) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));  // Hot Red
        if (pct >= 15) return new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Amber
        return new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248));                 // Sky Blue
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
