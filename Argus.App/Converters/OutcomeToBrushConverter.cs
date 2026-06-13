using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Argus.App.Converters;

public sealed class OutcomeToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(255, 0, 204, 102));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(255, 255, 79, 90));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromArgb(255, 255, 200, 87));
    private static readonly SolidColorBrush GreyBrush = new(Color.FromArgb(255, 168, 183, 196));

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string outcome)
        {
            return outcome.ToLowerInvariant() switch
            {
                "succeeded" => GreenBrush,
                "failed" => RedBrush,
                "started" => AmberBrush,
                "cancelled" => GreyBrush,
                _ => GreyBrush
            };
        }
        return GreyBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
