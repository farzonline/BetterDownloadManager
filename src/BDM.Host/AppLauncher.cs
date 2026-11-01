using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;

namespace BDM.Host;

public record LaunchResult(bool Running, bool Started);

public static class AppLauncher
{
    /// <summary>Makes sure something is listening on the app's pipe, starting the app when needed.</summary>
    public static async Task<LaunchResult> EnsureRunningAsync(string pipeName, string appExe, TimeSpan timeout, Action<string>? start = null)
    {
        if (await CanConnectAsync(pipeName, 300)) return new(true, false);
        try { (start ?? DefaultStart)(appExe); }
        catch (Exception e) when (e is Win32Exception or FileNotFoundException or InvalidOperationException) { return new(false, false); }
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (await CanConnectAsync(pipeName, 200)) return new(true, true);
            await Task.Delay(100);
        }
        return new(false, true);
    }

    public static Task<bool> IsRunningAsync(string pipeName) => CanConnectAsync(pipeName, 300);

    static void DefaultStart(string exe) => Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });

    // Connect-and-leave probe: the app's pipe server must tolerate an empty connection.
    static async Task<bool> CanConnectAsync(string pipeName, int ms)
    {
        try
        {
            await using var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await p.ConnectAsync(ms);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (IOException) { return false; }
    }
}
