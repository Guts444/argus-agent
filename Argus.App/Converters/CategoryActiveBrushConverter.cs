using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace Argus.App.Converters;

public sealed class CategoryActiveBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var currentCategory = value as string ?? string.Empty;
        var targetCategory = parameter as string ?? string.Empty;

        var isActive = string.Equals(currentCategory, targetCategory, StringComparison.OrdinalIgnoreCase);

        if (isActive)
        {
            return Application.Current.Resources["ArgusAccentBrush"] as Brush ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 247, 255));
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
