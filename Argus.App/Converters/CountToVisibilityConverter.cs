using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

/// <summary>
/// Converts an integer count to Visibility.Visible (if count > 0) or Visibility.Collapsed.
/// </summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var count = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        return count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
