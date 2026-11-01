using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BDM.App.Views;

public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageHeader), new PropertyMetadata("", (d, _) => ((PageHeader)d).Refresh()));
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(nameof(Hue), typeof(Color), typeof(PageHeader), new PropertyMetadata(Colors.SlateGray, (d, _) => ((PageHeader)d).Refresh()));

    public PageHeader()
    {
        InitializeComponent();
        Refresh();
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public Color Hue { get => (Color)GetValue(HueProperty); set => SetValue(HueProperty, value); }

    static Color Shade(Color c, double towardsBlack) =>
        Color.FromRgb((byte)(c.R * (1 - towardsBlack)), (byte)(c.G * (1 - towardsBlack)), (byte)(c.B * (1 - towardsBlack)));

    void Refresh()
    {
        if (Icon == null) return; // before InitializeComponent finished
        var hue = Hue;
        Icon.Text = Glyph;
        Mark.Text = Glyph;
        Mark.Foreground = new SolidColorBrush(hue);
        TitleText.Text = Title;
        SubtitleText.Text = Subtitle;
        SubtitleText.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
        Tile.Background = new LinearGradientBrush(hue, Shade(hue, 0.3), 45);
        TileGlow.Color = hue;
        Wash.Background = new LinearGradientBrush(
            new GradientStopCollection { new(Color.FromArgb(0x48, hue.R, hue.G, hue.B), 0), new(Color.FromArgb(0x14, hue.R, hue.G, hue.B), 0.6), new(Color.FromArgb(0, hue.R, hue.G, hue.B), 1) },
            new Point(0, 0), new Point(1, 0.4));
    }
}
