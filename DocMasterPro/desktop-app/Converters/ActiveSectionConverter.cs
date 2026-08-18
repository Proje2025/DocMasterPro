using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DocConverter.Converters
{
    public class ActiveSectionBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int currentSection && parameter != null && int.TryParse(parameter.ToString(), out int targetSection))
            {
                if (currentSection == targetSection)
                {
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563EB"));
                }
            }

            return new SolidColorBrush(Colors.Transparent);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ActiveSectionForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int currentSection && parameter != null && int.TryParse(parameter.ToString(), out int targetSection))
            {
                if (currentSection == targetSection)
                {
                    return new SolidColorBrush(Colors.White);
                }
            }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
