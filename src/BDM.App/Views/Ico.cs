using System.Windows;

namespace BDM.App.Views;

/// <summary>Put an icon in front of a button's label: <c>&lt;Button v:Ico.Glyph="&amp;#xE896;" Content="Download"/&gt;</c>.</summary>
public static class Ico
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Ico), new PropertyMetadata(""));
    public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
    public static void SetGlyph(DependencyObject d, string value) => d.SetValue(GlyphProperty, value);
}
