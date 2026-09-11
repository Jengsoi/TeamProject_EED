using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SafetyVision.Client.Converters;

// 착용/정상=초록, 미착용/점검 필요=주황, 미확인=회색 (03_화면설계.md 공통 규칙).
public sealed class StatusToBrushConverter : IValueConverter
{
    public static readonly SolidColorBrush Worn = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    public static readonly SolidColorBrush NotWorn = new(Color.FromRgb(0xF5, 0x8B, 0x1F));
    public static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "WORN" or "NORMAL" => Worn,
        "NOT_WORN" or "CHECK_REQUIRED" or "UNKNOWN" or "UNCONFIRMED" => NotWorn,
        _ => Unknown
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value as string) switch
    {
        "WORN" => "착용",
        "NOT_WORN" => "미착용",
        "UNKNOWN" => "미착용",
        "NORMAL" => "정상",
        "CHECK_REQUIRED" => "점검 필요",
        "UNCONFIRMED" => "미착용",
        _ => value?.ToString() ?? ""
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// Score(0~1)를 정수 백분율 문자열로. null(UNKNOWN)은 '—'.
public sealed class ScoreToPercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return $"{Math.Round(d * 100):0}%";
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// WornRatio(0~1, nullable)를 정수 백분율 문자열로. 0건이면 '—'.
public sealed class RatioToPercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d) return $"{Math.Round(d * 100):0}%";
        return "—";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RatioToPercentValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? d * 100.0 : 0.0;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// 문자열이 parameter와 같으면 Visible, 아니면 Collapsed.
public sealed class StringEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StringNotEqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Equals(value?.ToString(), parameter?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        bool invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        if (invert) isNull = !isNull;
        return isNull ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

// ColumnDefinition/RowDefinition.Width/Height(GridLength)를 ROI 비율로 채우기 위한 변환기.
public sealed class RatioToStarGridLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double ratio = value is double d ? d : 0;
        return new GridLength(Math.Max(0.0001, ratio) * 1000, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => value is bool b && !b;
}
