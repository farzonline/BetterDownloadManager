using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace BDM.Core.Engine;

/// <summary>Raises NetworkRestored when the network comes back or the PC wakes from sleep.</summary>
public sealed class ConnectivityWatcher : IDisposable
{
    readonly bool _listening;
    public event Action? NetworkRestored;

    public ConnectivityWatcher(bool listen = true)
    {
        _listening = listen;
        if (!listen) return;
        NetworkChange.NetworkAvailabilityChanged += OnAvailability;
        SystemEvents.PowerModeChanged += OnPower;
    }

    public void NotifyRestored() => NetworkRestored?.Invoke();

    void OnAvailability(object? s, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) NotifyRestored(); }

    async void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        await Task.Delay(TimeSpan.FromSeconds(3)); // let Wi-Fi reconnect before retrying
        NotifyRestored();
    }

    public void Dispose()
    {
        if (!_listening) return;
        NetworkChange.NetworkAvailabilityChanged -= OnAvailability;
        SystemEvents.PowerModeChanged -= OnPower;
    }
}
