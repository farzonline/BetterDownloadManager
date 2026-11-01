using System.Collections.Specialized;
using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;

namespace BDM.App.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        var vm = new HistoryViewModel(svc.Store);
        DataContext = vm;
        vm.Results.CollectionChanged += (_, _) => UpdateCount(vm);
        UpdateCount(vm);
        Loaded += (_, _) => SearchBox.Focus();
    }

    void UpdateCount(HistoryViewModel vm) => CountText.Text = vm.Results.Count == 1 ? "1 download" : $"{vm.Results.Count:N0} downloads";
}
