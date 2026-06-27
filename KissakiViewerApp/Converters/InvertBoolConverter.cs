using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KissakiViewer.Converters;

[ValueConversion(typeof(object), typeof(Visibility))]
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type t, object p, CultureInfo c)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b ? !b : value;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is bool b ? !b : value;
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public static readonly InvertBoolToVisibilityConverter Instance = new();

    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotImplementedException();
}
