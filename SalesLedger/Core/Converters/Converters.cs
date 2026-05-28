using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SalesLedger.Core.Theme;

namespace SalesLedger.Core.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return SolidColorBrush.Parse(AppTheme.TextPrimary); // Active color
            }
            return SolidColorBrush.Parse(AppTheme.TextMuted); // Inactive color
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToDecorationConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return new TextDecorationCollection();
            }
            return TextDecorations.Strikethrough;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PresetToColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return SolidColorBrush.Parse(AppTheme.AccentBlue); // Preset color
            }
            return SolidColorBrush.Parse(AppTheme.TextMuted); // Custom color
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class PresetToTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return "PRESET";
            }
            return "CUSTOM";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ActiveToStatusTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return "Deactivate";
            }
            return "Activate";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value != null && parameter is string target)
            {
                return string.Equals(value.ToString(), target, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter is string target)
            {
                if (targetType.IsEnum)
                {
                    return Enum.Parse(targetType, target, true);
                }
                return target;
            }
            return value!;
        }
    }

    public class CalculationTypeToTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Models.PayoutType type)
            {
                switch (type)
                {
                    case Models.PayoutType.PercentageOfPrice:
                        return "Percentage of Price";
                    case Models.PayoutType.FlatRate:
                        return "Flat Rate";
                    case Models.PayoutType.PercentageOfNetProfit:
                        return "Percentage of Net Margin";
                    default:
                        return type.ToString();
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isError && isError)
            {
                return SolidColorBrush.Parse(AppTheme.AccentRed); // Red
            }
            return SolidColorBrush.Parse(AppTheme.AccentGreen); // Green
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
