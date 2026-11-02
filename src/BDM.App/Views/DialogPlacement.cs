using System.Windows;

namespace BDM.App.Views;

static class DialogPlacement
{
    /// <summary>
    /// Pops a dialog up in front of everything (IDM does the same for a download from the browser): over the main window
    /// when it is on screen, otherwise centred and on the taskbar. Windows doesn't let a background app take the focus,
    /// so the dialog stays on top until the owner has clicked it (the browser host lets BDM take the focus when it can).
    /// </summary>
    public static void Place(Window w)
    {
        // A dialog owned by a minimized window is hidden with it: own it only when the main window can be seen.
        if (Application.Current.MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } main && !ReferenceEquals(main, w))
            w.Owner = main;
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            w.ShowInTaskbar = true;
        }
        w.Topmost = true;
        w.Activated += (_, _) => w.Topmost = false;
        w.ContentRendered += (_, _) => w.Activate();
    }
}
