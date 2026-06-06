using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Argus.App.Converters;

public sealed class RoleVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var role = value as string ?? string.Empty;
        var param = parameter as string ?? string.Empty;

        if (string.Equals(param, "thinking", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(role, "thinking", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (string.Equals(param, "message", StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(role, "thinking", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
