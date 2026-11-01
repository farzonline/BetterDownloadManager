using BDM.App.Services;
using BDM.App.Settings;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Ext;
using BDM.Core.Ipc;
using BDM.Core.Net;

namespace BDM.App.Tests;

public class ProxyProviderTests
{
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    static (ProxyClientProvider P, ProxyProfileStore Profiles, CredentialVault Vault) Make()
    {
        var dir = Tmp();
        var profiles = new ProxyProfileStore(Path.Combine(dir, "profiles.json"));
        var vault = new CredentialVault(Path.Combine(dir, "vault.json"));
        profiles.Save([new ProxyProfile("p1", "Work", ProxyKind.Http, "127.0.0.1", 3128, null)]);
        return (new ProxyClientProvider(profiles, vault), profiles, vault);
    }

    [Fact]
    public void Invalidate_LeavesClientsThatDownloadsAreStillUsingAlone()
    {
        var (p, _, _) = Make();
        var inUse = p.Get("p1");

        p.Invalidate();

        // A disposed HttpClient refuses to be configured; an in-flight download would have failed the same way.
        Assert.Null(Record.Exception(() => inUse.Timeout = TimeSpan.FromMinutes(10)));
        Assert.NotSame(inUse, p.Get("p1")); // the next download builds a fresh client from the current profile
    }

    [Fact]
    public void TheBrowserRoute_UsesTheRelayClient_AndIsOfferedAsAChoice()
    {
        var (p, _, _) = Make();
        Assert.DoesNotContain(p.Choices(), c => c.Id == BrowserRelay.ProfileId); // no relay: nothing to offer
        var relay = new HttpClient();
        p.Browser = relay;
        Assert.Same(relay, p.Get(BrowserRelay.ProfileId));
        Assert.Contains(p.Choices(), c => c.Id == BrowserRelay.ProfileId);
    }

    [Fact]
    public void Get_ForADeletedProfile_Throws_InsteadOfSilentlyGoingDirect()
    {
        var (p, profiles, _) = Make();
        profiles.Save([]);
        p.Invalidate();

        var e = Assert.Throws<InvalidOperationException>(() => p.Get("p1"));
        Assert.Contains("no longer exists", e.Message);
        Assert.NotNull(p.Get(null)); // "no proxy" is still fine
    }

    [Fact]
    public async Task AddPage_ForADeletedProxyProfile_RepliesWithTheReasonInsteadOfCrashing()
    {
        var (_, profiles, vault) = Make();
        var store = new DownloadStore(Path.Combine(Tmp(), "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None));
        var m = new DownloadManager(store, _ => new HttpClient()) { GlobalProxyId = "gone", YtDlp = new YtDlpRunner(() => "yt-dlp.exe", _ => throw new InvalidOperationException("must not run")) };
        m.ProxyUrlFor = id => ProxyUrls.For(id, profiles, vault);
        var h = new HostMessageHandler(m, new AppSettings { DownloadRoot = Tmp() }, new NoPicker());

        var reply = await h.HandleAsync(new HostMessage("addPage", "https://video.example/w", null, "t", null, null, null, null));

        Assert.False(reply.Ok);
        Assert.Contains("no longer exists", reply.Error);
        Assert.Empty(m.All());
    }

    sealed class NoPicker : IQualityPicker
    {
        public Task<BDM.Core.Streams.StreamChoice?> PickAsync(IReadOnlyList<BDM.Core.Streams.StreamChoice> choices, string title) => Task.FromResult<BDM.Core.Streams.StreamChoice?>(null);
    }
}
