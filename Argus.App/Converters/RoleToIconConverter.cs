using Microsoft.UI.Xaml.Data;

namespace Argus.App.Converters;

public sealed class RoleToIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string role)
        {
            if (string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return "\xE77B"; // Person icon
            }
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return "\xE9D2"; // Brain/AI icon
            }
        }
        return "\xE8BD"; // Default message/chat bubble icon
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
