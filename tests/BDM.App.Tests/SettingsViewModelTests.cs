using BDM.App.Services;
using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Batch;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Net;
using Microsoft.Win32;

namespace BDM.App.Tests;

public class SettingsViewModelTests
{
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    static DownloadStore NewStore() => new(Path.Combine(Tmp(), "bdm.db"));

    static DownloadManager NewManager(DownloadStore? store = null)
    {
        store ??= NewStore();
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None)); // nothing starts
        return new DownloadManager(store, _ => new HttpClient());
    }

    // ---- options ----
    [Fact]
    public void Connections_ClampedTo1To32()
    {
        var vm = new OptionsViewModel(new AppSettings());
        Assert.Equal(8, vm.Connections);
        vm.Connections = 99; Assert.Equal(32, vm.Connections);
        vm.Connections = 0; Assert.Equal(1, vm.Connections);
        vm.Connections = -5; Assert.Equal(1, vm.Connections);
        vm.Connections = 16; Assert.Equal(16, vm.Connections);
    }

    [Fact]
    public void Options_SaveWritesBackIntoSettings_AndLoadsThemBack()
    {
        var s = new AppSettings();
        var vm = new OptionsViewModel(s)
        {
            Connections = 12, SpeedLimitKBps = 500, AntivirusCommand = "scan.exe {file}", FfmpegPath = @"C:\ff\ffmpeg.exe",
            ThemeMode = "Dark", AlwaysAskQuality = true, DownloadRoot = @"D:\Dl", MinimizeToTray = false, WatchClipboard = false,
            GlobalProxyId = "p1",
        };
        vm.CategoryFolders["Video"] = @"E:\Movies";
        vm.Apply();
        Assert.Equal(12, s.Connections);
        Assert.Equal(500, s.SpeedLimitKBps);
        Assert.Equal("scan.exe {file}", s.AntivirusCommand);
        Assert.Equal("Dark", s.ThemeMode);
        Assert.True(s.AlwaysAskQuality);
        Assert.False(s.MinimizeToTray);
        Assert.Equal("p1", s.GlobalProxyId);
        Assert.Equal(@"E:\Movies", s.CategoryFolders["Video"]);
        Assert.Equal(@"E:\Movies", Categories.FolderFor(s, "Video"));
        Assert.Equal(@"D:\Dl\Music", Categories.FolderFor(s, "Music"));
        Assert.Equal(12, new OptionsViewModel(s).Connections);
    }

    [Fact]
    public void Options_RejectsNegativeSpeedAndBlankFolders()
    {
        var vm = new OptionsViewModel(new AppSettings()) { SpeedLimitKBps = -10 };
        Assert.Equal(0, vm.SpeedLimitKBps);
        vm.DownloadRoot = "  ";
        Assert.False(vm.Apply());
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task SettingsApplier_PushesSettingsIntoTheManager()
    {
        var store = NewStore();
        var m = NewManager(store);
        var s = new AppSettings { Connections = 20, SpeedLimitKBps = 256, GlobalProxyId = "glob" };
        s.CategoryProxies["Video"] = "vid";
        SettingsApplier.Apply(s, m, store, antivirus: null);
        Assert.Equal("glob", m.GlobalProxyId);
        Assert.Equal("vid", m.CategoryProxies["Video"]);
        Assert.Equal(20, store.Queue("main")!.Connections);
        Assert.NotNull(m.GlobalLimiter);
        await m.GlobalLimiter!.AcquireAsync(1, default); // limiter exists and is usable
        s.SpeedLimitKBps = 0;
        SettingsApplier.Apply(s, m, store, antivirus: null);
        Assert.Null(m.CategoryProxies.GetValueOrDefault("Nope"));
    }

    [Fact]
    public async Task SettingsApplier_WiresTheAntivirusHook()
    {
        var store = NewStore();
        var m = NewManager(store);
        var calls = new List<string>();
        var s = new AppSettings { AntivirusCommand = "scan.exe /x {file}" };
        SettingsApplier.Apply(s, m, store, (cmd, path) => { calls.Add(cmd + "|" + path); return Task.CompletedTask; });
        var rec = new DownloadRecord(Guid.NewGuid(), new BDM.Core.Http.DownloadRequest(new Uri("https://x/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, "."),
            DownloadState.Completed, 1, 1, null, "main", DateTime.UtcNow, FilePath: @"C:\Dl\a.zip");
        await m.OnCompleted!(rec);
        Assert.Equal(new[] { @"scan.exe /x {file}|C:\Dl\a.zip" }, calls);
        s.AntivirusCommand = null;
        SettingsApplier.Apply(s, m, store, (cmd, path) => { calls.Add("again"); return Task.CompletedTask; });
        Assert.Null(m.OnCompleted); // no command configured: no hook
    }

    [Fact]
    public void StartWithWindows_WritesAndRemovesTheRunValue()
    {
        var sub = @"Software\BDMTest_" + Guid.NewGuid().ToString("N");
        try
        {
            StartupRegistration.Set(true, @"C:\Apps\BDM.App.exe", sub);
            using (var k = Registry.CurrentUser.OpenSubKey(sub)) Assert.Equal("\"C:\\Apps\\BDM.App.exe\" --background", k!.GetValue("BetterDownloadManager"));
            Assert.True(StartupRegistration.IsEnabled(sub));
            StartupRegistration.Set(false, @"C:\Apps\BDM.App.exe", sub);
            Assert.False(StartupRegistration.IsEnabled(sub));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); }
    }

    // ---- proxy / Surfshark ----
    static (ProxyProfilesViewModel Vm, string ProfilesPath, string VaultPath) NewProxyVm()
    {
        var dir = Tmp();
        var profilesPath = Path.Combine(dir, "profiles.json");
        var vaultPath = Path.Combine(dir, "vault.json");
        return (new ProxyProfilesViewModel(new ProxyProfileStore(profilesPath), new CredentialVault(vaultPath)), profilesPath, vaultPath);
    }

    [Fact]
    public void ImportSurfshark_AddsProfiles_NoPasswordInDatabase()
    {
        var (vm, profilesPath, vaultPath) = NewProxyVm();
        var added = vm.ImportSurfshark("# servers\nus-nyc.example.com:443\nMy UK,uk-lon.example.com,1080\n", username: "svc-user", password: "service-secret-123");
        Assert.Equal(2, added);
        Assert.Equal(2, vm.Items.Count);
        Assert.All(vm.Items, p => Assert.Equal("svc-user", p.Username));
        var json = File.ReadAllText(profilesPath);
        Assert.DoesNotContain("service-secret-123", json);
        Assert.DoesNotContain("service-secret-123", File.ReadAllText(vaultPath));
        Assert.Equal("service-secret-123", new CredentialVault(vaultPath).Load(vm.Items[0].VaultKey));
        Assert.Equal(2, new ProxyProfilesViewModel(new ProxyProfileStore(profilesPath), new CredentialVault(vaultPath)).Items.Count);
    }

    [Fact]
    public void ImportSurfshark_SkipsDuplicates_AndRemoveClearsTheSecret()
    {
        var (vm, _, vaultPath) = NewProxyVm();
        Assert.Equal(1, vm.ImportSurfshark("a.example.com:443", "u", "pw"));
        Assert.Equal(0, vm.ImportSurfshark("a.example.com:443", "u", "pw"));
        var p = vm.Items[0];
        vm.Remove(p);
        Assert.Empty(vm.Items);
        Assert.Null(new CredentialVault(vaultPath).Load(p.VaultKey));
    }

    [Fact]
    public void ProxyProfiles_AddSetPassword_AndRejectBadInput()
    {
        var (vm, _, vaultPath) = NewProxyVm();
        var p = vm.Add("Home", ProxyKind.Http, "proxy.local", 8080, "bob", "pw");
        Assert.Equal("Home", p.Name);
        vm.SetPassword(p, "newer");
        Assert.Equal("newer", new CredentialVault(vaultPath).Load(p.VaultKey));
        Assert.Throws<ArgumentException>(() => vm.Add("", ProxyKind.Http, "h", 80, null, null));
        Assert.Throws<ArgumentException>(() => vm.Add("x", ProxyKind.Http, "", 80, null, null));
        Assert.Throws<ArgumentException>(() => vm.Add("x", ProxyKind.Http, "h", 70000, null, null));
    }

    // ---- site logins ----
    [Fact]
    public void SiteLoginVm_NormalisesHost_AndAppliesToMatchingHostOnly()
    {
        var dir = Tmp();
        var logins = new SiteLogins(Path.Combine(dir, "l.json"), new CredentialVault(Path.Combine(dir, "v.json")));
        var vm = new SitesLoginViewModel(logins);
        vm.Save("https://Files.Example.com:8443/login?x=1", "bob", "pw");
        Assert.Equal("files.example.com", Assert.Single(vm.Items).Host);
        Assert.NotNull(logins.AuthorizationFor(new Uri("https://files.example.com/a")));
        Assert.NotNull(logins.AuthorizationFor(new Uri("https://deep.files.example.com/a")));
        Assert.Null(logins.AuthorizationFor(new Uri("https://example.com/a")));
        Assert.Throws<ArgumentException>(() => vm.Save("", "bob", "pw"));
        Assert.Throws<ArgumentException>(() => vm.Save("a.com", "", "pw"));
        vm.Remove(vm.Items[0]);
        Assert.Empty(vm.Items);
    }

    // ---- queues / scheduler ----
    [Fact]
    public void QueueEditor_ParsesTimesAndSaves()
    {
        var store = NewStore();
        var vm = new QueuesViewModel(store);
        var night = vm.Add("Night");
        night.StartAtText = "23:00";
        night.StopAtText = "06:30";
        night.MaxConcurrent = 2;
        night.Connections = 4;
        night.OnFinish = PostAction.Sleep;
        Assert.True(vm.Save(night));
        var saved = store.Queue(night.Id)!;
        Assert.Equal(TimeSpan.FromHours(23), saved.StartAt);
        Assert.Equal(new TimeSpan(6, 30, 0), saved.StopAt);
        Assert.Equal(PostAction.Sleep, saved.OnFinish);
        night.StartAtText = "";
        Assert.True(vm.Save(night));
        Assert.Null(store.Queue(night.Id)!.StartAt);
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("7pm")]
    [InlineData("12:75")]
    public void QueueEditor_RejectsBadTimes(string text)
    {
        var vm = new QueuesViewModel(NewStore());
        var q = vm.Add("Bad");
        q.StartAtText = text;
        Assert.False(vm.Save(q));
        Assert.NotNull(q.Error);
    }

    [Fact]
    public void QueueEditor_MainQueueCannotBeDeleted_AndQueueWithDownloadsNeitherCan()
    {
        var store = NewStore();
        var m = NewManager(store);
        var vm = new QueuesViewModel(store);
        Assert.False(vm.Delete(vm.Items.First(q => q.Id == "main"), m));
        var extra = vm.Add("Extra");
        m.Add(new BDM.Core.Http.DownloadRequest(new Uri("https://x/a.zip"), null, null, new Dictionary<string, string>(), null, null, null, "."), extra.Id);
        Assert.False(vm.Delete(extra, m));
        m.Remove(m.All()[0].Id, false);
        Assert.True(vm.Delete(extra, m));
        Assert.Null(store.Queue(extra.Id));
    }

    // ---- batch ----
    [Fact]
    public void Batch_PreviewsCount_AndExplainsProblems()
    {
        var vm = new BatchViewModel();
        vm.Pattern = "https://x.example/img[001-120].jpg";
        Assert.Equal(120, vm.Count);
        Assert.Equal("https://x.example/img001.jpg", vm.PreviewLines[0]);
        Assert.Null(vm.Error);
        vm.Pattern = "https://x.example/[0-99999]/[0-99999]";
        Assert.Equal(0, vm.Count);
        Assert.Contains("10,000", vm.Error);
        vm.Pattern = "not a url [1-3]";
        Assert.Equal(0, vm.Count);
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public void Batch_AddAll_QueuesEveryExpandedAddress()
    {
        var m = NewManager();
        var main = new MainViewModel(m, new AppSettings { DownloadRoot = Tmp() }, new NoShell(), a => a());
        var vm = new BatchViewModel { Pattern = "https://x.example/p[1-3].zip" };
        Assert.Equal(3, vm.AddAll(main));
        Assert.Equal(3, main.Items.Count);
        Assert.Equal(new[] { "p1.zip", "p2.zip", "p3.zip" }, main.Items.Select(i => i.Name));
    }

    sealed class NoShell : IShell { public void Open(string path) { } public void ShowInFolder(string path) { } }

    // ---- grabber ----
    sealed class FakeSite(Dictionary<string, string> pages) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (pages.TryGetValue(request.RequestUri!.AbsolutePath, out var html))
                return Task.FromResult(new HttpResponseMessage { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Grabber_ListsFilesAndAddsOnlyTheSelectedOnes()
    {
        var http = new HttpClient(new FakeSite(new() { ["/"] = "<a href='/a.zip'>a</a><a href='/b.zip'>b</a><a href='/pic.jpg'>p</a>" }));
        var m = NewManager();
        var main = new MainViewModel(m, new AppSettings { DownloadRoot = Tmp() }, new NoShell(), a => a());
        var vm = new GrabberViewModel { StartUrl = "https://site.example/", Depth = 0, IncludeExtensions = "zip jpg", ExcludeExtensions = "jpg" };

        await vm.CrawlAsync(http, default);

        Assert.Equal(new[] { "a.zip", "b.zip" }, vm.Results.Select(r => r.Name));
        Assert.All(vm.Results, r => Assert.True(r.IsSelected));
        vm.Results[1].IsSelected = false;
        Assert.Equal(1, vm.AddSelected(main));
        Assert.Equal("a.zip", Assert.Single(main.Items).Name);
    }

    [Fact]
    public async Task Grabber_BadStartUrl_ReportsInsteadOfThrowing()
    {
        var vm = new GrabberViewModel { StartUrl = "nonsense" };
        await vm.CrawlAsync(new HttpClient(new FakeSite(new())), default);
        Assert.Empty(vm.Results);
        Assert.NotNull(vm.Error);
    }

    // ---- history + export/import ----
    [Fact]
    public void History_SearchesTheStore()
    {
        var store = NewStore();
        var m = NewManager(store);
        foreach (var n in new[] { "holiday.mp4", "report.pdf" })
            m.Add(new BDM.Core.Http.DownloadRequest(new Uri("https://x.example/" + n), null, null, new Dictionary<string, string>(), null, null, null, "."));
        var vm = new HistoryViewModel(store);
        Assert.Equal(2, vm.Results.Count);
        vm.SearchText = "holi";
        Assert.Equal("holiday.mp4", Assert.Single(vm.Results).Name);
        vm.SearchText = "x.example/report";
        Assert.Equal("report.pdf", Assert.Single(vm.Results).Name);
    }

    [Fact]
    public void Settings_ExportImport_RoundTrip()
    {
        var store = NewStore();
        var m = NewManager(store);
        m.Add(new BDM.Core.Http.DownloadRequest(new Uri("https://x.example/a.zip"), null, null, new Dictionary<string, string>(), null, null, "Compressed", "."));
        store.SaveQueue(new QueueDefinition("night", "Night", 1, 2, TimeSpan.FromHours(23), null, PostAction.Shutdown, "p1"));
        var settings = new AppSettings { Connections = 14, DownloadRoot = @"D:\Dl", ThemeMode = "Dark", AntivirusCommand = "av.exe {file}", GlobalProxyId = "p1" };
        settings.CategoryFolders["Video"] = @"E:\V";
        var profiles = new[] { new ProxyProfile("p1", "NYC", ProxyKind.Socks5, "h", 1080, "svc") };
        var dir = Tmp();
        var logins = new SiteLogins(Path.Combine(dir, "l.json"), new CredentialVault(Path.Combine(dir, "v.json")));
        logins.Set("example.com", "bob", "never-exported-pw");

        var json = SettingsBundle.Export(settings, profiles, logins.All(), store);
        Assert.DoesNotContain("never-exported-pw", json);

        var settings2 = new AppSettings();
        var store2 = NewStore();
        var profileStore2 = new ProxyProfileStore(Path.Combine(Tmp(), "p.json"));
        var result = SettingsBundle.Import(json, settings2, profileStore2, store2);

        Assert.Equal(14, settings2.Connections);
        Assert.Equal(@"D:\Dl", settings2.DownloadRoot);
        Assert.Equal("Dark", settings2.ThemeMode);
        Assert.Equal("av.exe {file}", settings2.AntivirusCommand);
        Assert.Equal(@"E:\V", settings2.CategoryFolders["Video"]);
        Assert.Equal("p1", Assert.Single(profileStore2.Load()).Id);
        Assert.Equal(PostAction.Shutdown, store2.Queue("night")!.OnFinish);
        Assert.Equal("p1", store2.Queue("night")!.ProxyProfileId);
        Assert.Single(store2.All());
        Assert.Equal(new[] { "example.com" }, result.LoginHosts); // usernames come back; passwords must be re-entered
    }

    [Fact]
    public void Settings_Import_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => SettingsBundle.Import("{ nope", new AppSettings(), new ProxyProfileStore(Path.Combine(Tmp(), "p.json")), NewStore()));
        Assert.Throws<FormatException>(() => SettingsBundle.Import("{\"Format\":\"something-else\"}", new AppSettings(), new ProxyProfileStore(Path.Combine(Tmp(), "p.json")), NewStore()));
    }
}
