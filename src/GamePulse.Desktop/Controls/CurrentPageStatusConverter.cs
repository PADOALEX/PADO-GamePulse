using System.Globalization;
using Avalonia.Data.Converters;
using GamePulse.Desktop.Resources;

namespace GamePulse.Desktop.Controls;

public sealed class CurrentPageStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? UiText.Get("CurrentPage") : "";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
