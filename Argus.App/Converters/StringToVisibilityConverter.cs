using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

/// <summary>
/// Converts a non-null, non-empty string to Visibility.Visible.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
