using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Streams;

namespace BDM.App.Services;

public sealed class ExplorerShell : IShell
{
    public void Open(string path)
    {
        if (!File.Exists(path)) { MessageBox.Show($"This file is not there any more:\n{path}", "Better Download Manager"); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void ShowInFolder(string path)
    {
        var args = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{Path.GetDirectoryName(path)}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }
}

/// <summary>Shows the quality chooser on the UI thread (requests arrive on pipe threads).</summary>
public sealed class QualityPickerService : IQualityPicker
{
    public Task<StreamChoice?> PickAsync(IReadOnlyList<StreamChoice> choices, string title) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Views.QualityPickerDialog(choices, title);
            if (Application.Current.MainWindow is { IsVisible: true } main) dlg.Owner = main;
            return dlg.ShowDialog() == true ? dlg.Selected : null;
        }).Task;
}

/// <summary>Shows the download dialog for a file caught in the browser, on the UI thread, even while BDM sits in the tray.</summary>
public sealed class DownloadPromptService(AppSettings settings, ProxyClientProvider clients) : IDownloadPrompt
{
    public Task<DownloadPlan?> AskAsync(DownloadPlan proposal, Core.Http.DownloadRequest template) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new Views.AddUrlDialog(clients.Get(null), settings, null, clients.Choices("Use the default"), proposal, template);
            Views.DialogPlacement.Place(dlg);
            return dlg.ShowDialog() == true ? dlg.Plan : null;
        }).Task;
}

/// <summary>Watches the clipboard (WM_CLIPBOARDUPDATE) and reports links worth downloading.</summary>
public sealed class ClipboardWatcher : IDisposable
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    readonly ClipboardUrlFilter _filter;
    readonly Func<bool> _enabled;
    HwndSource? _source;

    public event Action<Uri>? UrlCopied;

    public ClipboardWatcher(Window owner, ClipboardUrlFilter filter, Func<bool> enabled)
    {
        _filter = filter;
        _enabled = enabled;
        var hwnd = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(hwnd);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && _enabled() && !CopiedByThisApp())
        {
            string? text = null;
            for (var attempt = 0; attempt < 3 && text == null; attempt++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; break; }
                catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(30); } // another app holds the clipboard
            }
            if (_filter.TryGet(text, out var url) && url != null) UrlCopied?.Invoke(url);
        }
        return IntPtr.Zero;
    }

    /// <summary>"Copy address" in BDM must not offer that link back as a new download.</summary>
    static bool CopiedByThisApp()
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var pid) != 0 && pid == (uint)Environment.ProcessId;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public void Dispose()
    {
        if (_source != null) { RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(WndProc); _source = null; }
    }
}

/// <summary>Notification-area icon. Uses WinForms' NotifyIcon, the only built-in tray API.</summary>
public sealed class TrayHost : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayHost(Action show, Action addUrl, Action toggleDropTarget, Action exit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Better Download Manager", null, (_, _) => show());
        menu.Items.Add("Add URL…", null, (_, _) => addUrl());
        menu.Items.Add("Show or hide drop target", null, (_, _) => toggleDropTarget());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = AppIcon.TrayIcon(), Text = "Better Download Manager", Visible = true, ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => show();
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(4000, title, text, System.Windows.Forms.ToolTipIcon.Info);

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
