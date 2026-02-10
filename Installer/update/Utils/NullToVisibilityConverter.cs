using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace update.Utils
{
    public class NullToVisibilityConverter : IValueConverter
    {

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = (parameter as string)?.Equals("Invert", StringComparison.OrdinalIgnoreCase) == true;
            bool visible = value != null;
            if (invert) visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
