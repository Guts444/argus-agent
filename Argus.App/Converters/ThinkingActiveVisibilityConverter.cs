using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace Argus.App.Converters;

public sealed class ThinkingActiveVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var content = value as string ?? string.Empty;

        var isActive = content.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
                       content.StartsWith("Searching the web", StringComparison.OrdinalIgnoreCase);

        return isActive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
