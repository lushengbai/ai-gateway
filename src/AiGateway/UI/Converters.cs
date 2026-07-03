using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AiGateway.UI;

/// <summary>Returns the logical negation of a boolean (e.g. enable a control only when stopped).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>Formats a byte count as a compact human-readable string (e.g. "12.3 KB").</summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0,
        };
        if (bytes <= 0) return "—";

        string[] units = { "B", "KB", "MB", "GB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colors an HTTP status code: green 2xx, amber 3xx/4xx, red 5xx, grey pending.</summary>
public sealed class StatusCodeToBrushConverter : IValueConverter
{
    private static readonly Brush Ok      = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush Warn    = new SolidColorBrush(Color.FromRgb(0xB5, 0x8A, 0x00));
    private static readonly Brush Error   = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly Brush Pending = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int code = value is int i ? i : 0;
        return code switch
        {
            >= 200 and < 300 => Ok,
            >= 300 and < 500 => Warn,
            >= 500 => Error,
            _ => Pending,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Displays the status code, or "…" while a request is still in flight.</summary>
public sealed class StatusCodeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int code = value is int i ? i : 0;
        return code == 0 ? "…" : code.ToString(CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Formats elapsed milliseconds, or blank while pending.</summary>
public sealed class ElapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ms = value is double d ? d : 0;
        return ms <= 0 ? "—" : $"{ms:0} ms";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
