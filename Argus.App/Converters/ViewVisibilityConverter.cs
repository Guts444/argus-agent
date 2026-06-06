using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

public sealed class ViewVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var currentView = value as string;
        var targetView = parameter as string;
        var invert = targetView?.StartsWith('!') == true;

        if (invert)
        {
            targetView = targetView![1..];
        }

        var matches = string.Equals(currentView, targetView, StringComparison.OrdinalIgnoreCase);
        return matches != invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
