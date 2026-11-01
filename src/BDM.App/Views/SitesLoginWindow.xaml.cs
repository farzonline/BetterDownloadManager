using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Net;

namespace BDM.App.Views;

public partial class SitesLoginWindow : Window
{
    readonly SitesLoginViewModel _vm;

    public SitesLoginWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _vm = new SitesLoginViewModel(svc.Logins);
        DataContext = _vm;
        List.SelectionChanged += (_, _) =>
        {
            if (List.SelectedItem is SiteLogin l) { HostBox.Text = l.Host; UserBox.Text = l.Username; PasswordBox.Clear(); }
        };
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        try
        {
            _vm.Save(HostBox.Text, UserBox.Text, PasswordBox.Password);
            PasswordBox.Clear();
        }
        catch (ArgumentException ex) { ErrorText.Text = ex.Message; }
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is SiteLogin l) { _vm.Remove(l); HostBox.Clear(); UserBox.Clear(); PasswordBox.Clear(); }
    }
}
