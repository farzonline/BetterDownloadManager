using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BDM.App.Services;

namespace BDM.App.Views;

/// <summary>Sidebar entry or category name -> its icon.</summary>
public sealed class CategoryGlyphConverter : IValueConverter
{
    public static CategoryGlyphConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Categories.Glyph(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Category name -> one of its colours; the parameter is Brush (solid), Tint (soft fill) or Fill (gradient).</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    public static CategoryBrushConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        HueBrushConverter.Find(Categories.Hue(value as string ?? ""), parameter as string ?? "Brush");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Colour name (Indigo, Rose...) -> its brush, same parameters as <see cref="CategoryBrushConverter"/>.</summary>
public sealed class HueBrushConverter : IValueConverter
{
    public static HueBrushConverter Instance { get; } = new();

    public static Brush Find(string hue, string kind) =>
        Application.Current?.TryFindResource($"Hue.{hue}.{kind}") as Brush ?? Brushes.Transparent;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Find(value as string ?? "", parameter as string ?? "Brush");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
