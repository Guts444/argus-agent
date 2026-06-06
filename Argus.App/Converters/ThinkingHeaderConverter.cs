using Microsoft.UI.Xaml.Data;
using System;

namespace Argus.App.Converters;

public sealed class ThinkingHeaderConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var content = value as string ?? string.Empty;

        if (content.StartsWith("Thinking", StringComparison.OrdinalIgnoreCase) ||
            content.StartsWith("Searching the web", StringComparison.OrdinalIgnoreCase))
        {
            var firstNewLine = content.IndexOf('\n');
            return firstNewLine >= 0 ? content[..firstNewLine] : content;
        }

        return "Thinking Process";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
