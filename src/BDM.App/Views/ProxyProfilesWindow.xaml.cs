using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Net;

namespace BDM.App.Views;

public partial class ProxyProfilesWindow : Window
{
    readonly AppServices _svc;
    readonly ProxyProfilesViewModel _vm;

    public ProxyProfilesWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _svc = svc;
        _vm = new ProxyProfilesViewModel(svc.Profiles, svc.Vault);
        DataContext = _vm;
        Closed += (_, _) => _svc.Clients.Invalidate(); // profiles or passwords may have changed
    }

    void Say(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Brush.Danger" : "Brush.TextMuted");
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(PortBox.Text, out var port)) throw new ArgumentException("The port must be a number.");
            var kind = KindBox.SelectedIndex == 1 ? ProxyKind.Socks5 : ProxyKind.Http;
            var p = _vm.Add(NameBox.Text, kind, HostBox.Text, port, UserBox.Text, PasswordBox.Password);
            NameBox.Clear(); HostBox.Clear(); UserBox.Clear(); PasswordBox.Clear();
            Say($"Added “{p.Name}”.");
        }
        catch (ArgumentException ex) { Say(ex.Message, error: true); }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var n = _vm.ImportSurfshark(ServerListBox.Text, ImportUserBox.Text, ImportPasswordBox.Password);
        ImportPasswordBox.Clear();
        Say(n == 0 ? "Nothing new to import. Check the list format." : $"Imported {n} server{(n == 1 ? "" : "s")}.", error: n == 0);
    }

    void SetPassword_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ProxyProfile p) { Say("Select a profile first.", error: true); return; }
        if (NewPasswordBox.Password.Length == 0) { Say("Type the new password first.", error: true); return; }
        _vm.SetPassword(p, NewPasswordBox.Password);
        NewPasswordBox.Clear();
        Say($"Password updated for “{p.Name}”.");
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not ProxyProfile p) return;
        var (ok, _) = ConfirmDialog.Ask(this, $"Remove “{p.Name}”?", "Downloads and queues set to use it will go direct. Its saved password is deleted.", "Remove profile", destructive: true);
        if (ok) { _vm.Remove(p); Say("Removed."); }
    }
}
