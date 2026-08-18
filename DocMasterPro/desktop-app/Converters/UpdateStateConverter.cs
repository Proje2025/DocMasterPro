using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using DocConverter.Models;

namespace DocConverter.Converters
{
    public class UpdateStateVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is UpdateState currentState && parameter is string targetStateStr)
            {
                // Can accept comma separated states, e.g. "Downloading,Installing"
                var targets = targetStateStr.Split(',');
                foreach (var target in targets)
                {
                    if (Enum.TryParse<UpdateState>(target.Trim(), ignoreCase: true, out var parsedState))
                    {
                        if (currentState == parsedState)
                            return Visibility.Visible;
                    }
                }
            }

            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
