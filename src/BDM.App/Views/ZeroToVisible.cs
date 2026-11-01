using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BDM.App.Views;

/// <summary>Visible when the bound count is 0 (empty-state captions), otherwise collapsed.</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public static ZeroToVisible Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
