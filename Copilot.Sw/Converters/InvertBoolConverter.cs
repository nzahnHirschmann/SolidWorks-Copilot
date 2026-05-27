using System;
using System.Globalization;
using System.Windows.Data;

namespace Copilot.Sw.Converters;

/// <summary>Boolean negation for WPF bindings.</summary>
public class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : false;
    }
}
