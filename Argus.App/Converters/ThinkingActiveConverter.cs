using Microsoft.UI.Xaml.Data;
using System;

namespace Argus.App.Converters;

public sealed class ThinkingActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var content = value as string ?? string.Empty;

        return content.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
               content.StartsWith("Searching the web", StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
