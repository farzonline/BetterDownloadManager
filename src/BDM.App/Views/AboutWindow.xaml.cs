using System.Windows;
using BDM.App.Services;

namespace BDM.App.Views;

/// <summary>Help > About: the app and its maker, nothing else.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        Logo.Source = AppIcon.Image(128);
        VersionText.Text = $"Version {typeof(AboutWindow).Assembly.GetName().Version?.ToString(3)}";
    }
}
