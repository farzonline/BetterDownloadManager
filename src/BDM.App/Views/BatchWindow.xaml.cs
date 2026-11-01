using System.ComponentModel;
using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;

namespace BDM.App.Views;

public partial class BatchWindow : Window
{
    readonly MainViewModel _main;
    readonly BatchViewModel _vm = new();

    public BatchWindow(MainViewModel main)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _main = main;
        DataContext = _vm;
        _vm.PropertyChanged += OnChanged;
        Loaded += (_, _) => PatternBox.Focus();
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        AddButton.IsEnabled = _vm.Count > 0;
        CountText.Text = _vm.Count == 0 ? "" : _vm.Count == 1 ? "1 address" : $"{_vm.Count:N0} addresses, starting with:";
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var n = _vm.AddAll(_main);
        DialogResult = true;
        (Owner as MainWindow)?.ShowBanner($"Added {n:N0} downloads.");
    }
}
