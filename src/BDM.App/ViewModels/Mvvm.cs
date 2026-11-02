using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BDM.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>ICommand without a WPF dependency; the view model calls <see cref="RaiseCanExecuteChanged"/> when its state changes.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Opening files and folders in Explorer; faked in tests.</summary>
public interface IShell
{
    void Open(string path);
    void ShowInFolder(string path);
}

public static class Format
{
    static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        return $"{v.ToString("0.0", CultureInfo.InvariantCulture)} {Units[u]}";
    }

    public static string Speed(double bytesPerSec) => bytesPerSec <= 0 ? "" : Size((long)bytesPerSec) + "/s";

    /// <summary>Blank when there is no sensible answer (a stalled download would need years, and TimeSpan overflows).</summary>
    public static string Eta(double seconds) => double.IsFinite(seconds) && seconds is >= 0 and < 100 * 86400 ? Eta(TimeSpan.FromSeconds(seconds)) : "";

    public static string Eta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s"
        : $"{Math.Max(0, (int)Math.Round(t.TotalSeconds))}s";
}
