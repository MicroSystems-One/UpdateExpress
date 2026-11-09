using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace UpdateExpress;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string hex = value as string ?? "";
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
                return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(0x6D, 0x4C, 0x41));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
