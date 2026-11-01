using System.Text.Json;
using BDM.Core.Ipc;

namespace BDM.Host;

public interface IAppLink
{
    Task<HostReply> SendAsync(HostMessage msg, CancellationToken ct);
}

/// <summary>Forwards messages to the app over its pipe, launching the app first if it is not running.</summary>
public sealed class PipeAppLink(string pipeName, string appExe, TimeSpan startTimeout, Action<string>? start = null, TimeSpan? replyTimeout = null) : IAppLink
{
    /// <summary>The app answers an "add" only after the owner has decided in its dialog, so this is generous: giving up early would make the browser download the file as well.</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromMinutes(5);

    public async Task<HostReply> SendAsync(HostMessage msg, CancellationToken ct)
    {
        // Asked whenever the browser wakes the extension: only answer if BDM is already running, never launch it for that.
        var state = msg.Op == "relayInfo"
            ? new LaunchResult(await AppLauncher.IsRunningAsync(pipeName), false)
            : await AppLauncher.EnsureRunningAsync(pipeName, appExe, startTimeout, start);
        if (!state.Running)
            return new HostReply(false, "BDM is not running and could not be started. The browser will download this file itself.", state.Started);
        try
        {
            var reply = await PipeClient.SendAsync(pipeName, msg, TimeSpan.FromSeconds(5), ct, replyTimeout ?? DefaultReplyTimeout);
            return reply with { AppStarted = state.Started };
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException)
        {
            return new HostReply(false, "BDM did not answer: " + e.Message, state.Started);
        }
    }
}

/// <summary>The host's main loop: framed JSON in from the browser, framed JSON reply out. Never crashes on bad input.</summary>
public sealed class HostRunner(IAppLink link)
{
    public async Task RunAsync(Stream stdin, Stream stdout, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await NativeMessaging.ReadAsync(stdin) is { } json)
        {
            HostReply reply;
            try
            {
                var msg = JsonSerializer.Deserialize<HostMessage>(json, PipeProtocol.Json) ?? throw new JsonException("empty message");
                reply = msg.Op == "ping" ? new HostReply(true, null, false) : await link.SendAsync(msg, ct);
            }
            catch (JsonException e) { reply = new HostReply(false, "Bad message: " + e.Message, false); }
            await NativeMessaging.WriteAsync(stdout, JsonSerializer.Serialize(reply, PipeProtocol.Json));
        }
    }
}
