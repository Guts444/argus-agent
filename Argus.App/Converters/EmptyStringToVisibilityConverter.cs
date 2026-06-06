using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

public sealed class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is string text && !string.IsNullOrWhiteSpace(text);
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
