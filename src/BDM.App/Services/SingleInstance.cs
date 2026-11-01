using BDM.Core.Ipc;

namespace BDM.App.Services;

/// <summary>Only one BDM runs per user; a second launch hands its arguments to the first and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    public bool IsFirst { get; }

    public SingleInstance(string id)
    {
        _mutex = new Mutex(true, $@"Local\BDM-{id}", out var created);
        IsFirst = created;
    }

    /// <summary>URLs among the arguments become "add" messages; with none, the first instance is asked to show its window.</summary>
    public async Task ForwardAsync(string pipeName, string[] args)
    {
        var urls = args.Where(a => Uri.TryCreate(a, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "ftp" or "ftps").ToList();
        if (urls.Count == 0)
            await PipeClient.SendAsync(pipeName, new HostMessage("activate", null, null, null, null, null, null, null), TimeSpan.FromSeconds(5));
        foreach (var u in urls)
            await PipeClient.SendAsync(pipeName, new HostMessage("add", u, null, null, null, null, null, null), TimeSpan.FromSeconds(5));
    }

    public void Dispose() => _mutex.Dispose();
}
