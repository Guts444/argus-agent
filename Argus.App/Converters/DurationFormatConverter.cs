using System;
using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

public sealed class DurationFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is long durationMs)
        {
            if (durationMs <= 0)
            {
                return "running...";
            }
            return $"{durationMs} ms";
        }
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
