using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace DocConverter.Converters
{
    /// <summary>
    /// DocumentItem.Status değerini bir arka plan rengine dönüştürür.
    /// </summary>
    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() switch
            {
                "Ready"      => new SolidColorBrush(Color.FromRgb(100, 149, 237)), // cornflower blue
                "Converting" => new SolidColorBrush(Color.FromRgb(255, 165,   0)), // orange
                "Done"       => new SolidColorBrush(Color.FromRgb( 34, 139,  34)), // forest green
                "Error"      => new SolidColorBrush(Color.FromRgb(220,  20,  60)), // crimson
                _            => new SolidColorBrush(Colors.Gray)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
