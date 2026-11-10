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

/// <summary>Category tree row margin: file types sit one step in, under All Downloads (as in IDM).</summary>
public sealed class CategoryIndentConverter : IValueConverter
{
    public static CategoryIndentConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new Thickness(Categories.Types.Contains(value as string ?? "") ? 22 : 2, 1, 2, 1);
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

/// <summary>File name -> the icon of the app Windows opens it with.</summary>
public sealed class FileIconConverter : IValueConverter
{
    public static FileIconConverter Instance { get; } = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => FileIcons.For(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
