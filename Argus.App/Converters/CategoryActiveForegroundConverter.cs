using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Argus.App.Converters;

public sealed class CategoryActiveForegroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var currentCategory = value as string ?? string.Empty;
        var targetCategory = parameter as string ?? string.Empty;

        var isActive = string.Equals(currentCategory, targetCategory, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            // Dark text color when background is bright cyan
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 6, 9, 16));
        }

        return Application.Current.Resources["ArgusTextBrush"] as Brush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 248, 255));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
