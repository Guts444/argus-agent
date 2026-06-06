using Microsoft.UI.Xaml.Data;
using System;

namespace Argus.App.Converters;

public sealed class ThinkingBodyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var content = value as string ?? string.Empty;

        if (content.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("Searching the web", StringComparison.OrdinalIgnoreCase))
        {
            var firstNewLine = content.IndexOf('\n');
            return firstNewLine >= 0 ? content[(firstNewLine + 1)..].Trim() : string.Empty;
        }

        return content;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
