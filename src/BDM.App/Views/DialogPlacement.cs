using System.Windows;

namespace BDM.App.Views;

static class DialogPlacement
{
    /// <summary>Over the main window when it is open; otherwise (BDM in the tray) centred, on top and on the taskbar so it isn't missed.</summary>
    public static void Place(Window w)
    {
        if (Application.Current.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, w))
        {
            w.Owner = main;
            return;
        }
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowInTaskbar = true;
        w.Topmost = true;
        w.Activated += (_, _) => w.Topmost = false; // on top only until the owner has seen it
    }
}
