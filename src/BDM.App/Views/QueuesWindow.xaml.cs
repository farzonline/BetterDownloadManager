using System.Windows;
using BDM.App.Services;
using BDM.App.ViewModels;
using BDM.Core.Engine;

namespace BDM.App.Views;

sealed record FinishChoice(PostAction Value, string Label)
{
    public override string ToString() => Label;
}

public partial class QueuesWindow : Window
{
    readonly AppServices _svc;
    readonly QueuesViewModel _vm;

    public QueuesWindow(AppServices svc)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        Icon = AppIcon.Image();
        _svc = svc;
        _vm = new QueuesViewModel(svc.Store);
        DataContext = _vm;
        FinishBox.ItemsSource = new[]
        {
            new FinishChoice(PostAction.None, "Do nothing"), new FinishChoice(PostAction.Sleep, "Put the PC to sleep"),
            new FinishChoice(PostAction.Hibernate, "Hibernate the PC"), new FinishChoice(PostAction.Shutdown, "Shut the PC down (30 s to cancel)"),
        };
        ProxyBox.ItemsSource = svc.Clients.Choices("Use the default");
        List.SelectedIndex = 0;
    }

    void New_Click(object sender, RoutedEventArgs e) => List.SelectedItem = _vm.Add("New queue");

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is QueueEditor q && _vm.Save(q)) (Owner as MainWindow)?.ShowBanner($"Saved “{q.Name}”.");
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not QueueEditor q) return;
        if (!_vm.Delete(q, _svc.Manager)) q.Error = q.Id == "main" ? "The main queue can't be deleted." : "Move or remove this queue's downloads first.";
        else List.SelectedIndex = 0;
    }
}
