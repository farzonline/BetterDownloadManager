using System.Windows;
using BDM.App.Services;

namespace BDM.App.Views;

/// <summary>Question with the object named, verb on the confirm button. For destructive actions Enter means Cancel.</summary>
public partial class ConfirmDialog : Window
{
    ConfirmDialog(string heading, string body, string confirmLabel, bool destructive, string? optionLabel)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        Heading.Text = heading;
        Body.Text = body;
        ConfirmButton.Content = confirmLabel;
        if (destructive)
        {
            ConfirmButton.Style = (Style)FindResource("Style.Button.DangerFill");
            CancelButton.IsDefault = true;   // Enter must never delete
        }
        else
        {
            ConfirmButton.Style = (Style)FindResource("Style.Button.Accent");
            ConfirmButton.IsDefault = true;
        }
        if (optionLabel != null) { Option.Content = optionLabel; Option.Visibility = Visibility.Visible; }
    }

    bool OptionChecked => Option.IsChecked == true;

    void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <returns>(confirmed, option checkbox state)</returns>
    public static (bool Confirmed, bool Option) Ask(Window? owner, string heading, string body, string confirmLabel, bool destructive, string? optionLabel = null)
    {
        var dlg = new ConfirmDialog(heading, body, confirmLabel, destructive, optionLabel);
        if (owner is { IsVisible: true }) dlg.Owner = owner; else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var ok = dlg.ShowDialog() == true;
        return (ok, ok && dlg.OptionChecked);
    }
}
