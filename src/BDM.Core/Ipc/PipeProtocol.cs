using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace BDM.Core.Ipc;

/// <summary>Op is "ping", "add" (plain URL) or "addStream" (HLS/DASH manifest; Kind = "hls"|"dash").</summary>
public record HostMessage(
    string Op,
    string? Url,
    string? PageUrl,
    string? Title,
    Dictionary<string, string>? Headers,
    string? Cookie,
    string? ManifestBody,
    string? Kind);

public record HostReply(bool Ok, string? Error, bool AppStarted);

public static class PipeProtocol
{
    /// <summary>camelCase, case-insensitive: the same JSON the extension speaks.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PipeName => "bdm-ipc-" + Environment.UserName;
}

/// <summary>One request line out, one reply line back, over the app's named pipe.</summary>
public static class PipeClient
{
    public static async Task<HostReply> SendAsync(string pipeName, HostMessage msg, TimeSpan connectTimeout, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);
        var w = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync(JsonSerializer.Serialize(msg, PipeProtocol.Json));
        using var r = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        var line = await r.ReadLineAsync(limit.Token);
        return line == null
            ? new HostReply(false, "BDM closed the connection without replying.", false)
            : JsonSerializer.Deserialize<HostReply>(line, PipeProtocol.Json)!;
    }
}
