using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using BDM.Core.Ipc;

namespace BDM.App.Services;

/// <summary>The app's named pipe: one JSON request line in, one reply line out. Empty connections (liveness probes) are ignored.</summary>
public sealed class PipeServer(string pipeName, Func<HostMessage, Task<HostReply>> handler) : IDisposable
{
    readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(AcceptLoop);

    async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                _ = Serve(pipe);
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { await Task.Delay(200); }
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                limit.CancelAfter(TimeSpan.FromMinutes(5)); // a quality picker may be open
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(limit.Token);
                if (string.IsNullOrWhiteSpace(line)) return; // liveness probe: connected and left

                HostReply reply;
                try
                {
                    var msg = JsonSerializer.Deserialize<HostMessage>(line, PipeProtocol.Json) ?? throw new JsonException("empty");
                    reply = await handler(msg);
                }
                catch (JsonException) { reply = new HostReply(false, "Bad message.", false); }
                catch (Exception e) { reply = new HostReply(false, e.Message, false); }

                var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply, PipeProtocol.Json));
                pipe.WaitForPipeDrain();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { /* client went away */ }
        }
    }

    public void Dispose() => _cts.Cancel();
}
