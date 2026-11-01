using BDM.Core.Engine;
using BDM.Core.Net;

namespace BDM.Core.Tests;

public class NetSupportTests
{
    static string Tmp() => Directory.CreateTempSubdirectory().FullName;

    static (SiteLogins Logins, string Path, CredentialVault Vault) NewLogins()
    {
        var dir = Tmp();
        var vault = new CredentialVault(Path.Combine(dir, "v.json"));
        var path = Path.Combine(dir, "logins.json");
        return (new SiteLogins(path, vault), path, vault);
    }

    [Fact]
    public void SiteLogin_AppliedToMatchingHostOnly()
    {
        var (l, _, _) = NewLogins();
        l.Set("example.com", "bob", "pw");
        Assert.Equal("Basic Ym9iOnB3", l.AuthorizationFor(new Uri("https://example.com/x")));
        Assert.Equal("Basic Ym9iOnB3", l.AuthorizationFor(new Uri("https://files.example.com/y")));
        Assert.Null(l.AuthorizationFor(new Uri("https://notexample.com/")));
        Assert.Null(l.AuthorizationFor(new Uri("https://example.org/")));
    }

    [Fact]
    public void SiteLogin_MostSpecificHostWins()
    {
        var (l, _, _) = NewLogins();
        l.Set("example.com", "bob", "pw");
        l.Set("files.example.com", "amy", "secret");
        Assert.Equal("Basic " + Convert.ToBase64String("amy:secret"u8.ToArray()), l.AuthorizationFor(new Uri("https://files.example.com/y")));
    }

    [Fact]
    public void SiteLogin_PasswordsAreNotInThePlainFile_AndSurviveReload()
    {
        var (l, path, vault) = NewLogins();
        l.Set("example.com", "bob", "super-secret-pw");
        Assert.DoesNotContain("super-secret-pw", File.ReadAllText(path));
        var reloaded = new SiteLogins(path, vault);
        Assert.Equal("bob", Assert.Single(reloaded.All()).Username);
        Assert.NotNull(reloaded.AuthorizationFor(new Uri("https://example.com/")));
    }

    [Fact]
    public void SiteLogin_RemoveAndMissingPassword()
    {
        var (l, path, vault) = NewLogins();
        l.Set("example.com", "bob", "pw");
        l.Remove("example.com");
        Assert.Empty(l.All());
        Assert.Null(l.AuthorizationFor(new Uri("https://example.com/")));
        // an entry whose vault secret is gone must not send half a credential
        File.WriteAllText(path, "[{\"Host\":\"example.com\",\"Username\":\"bob\"}]");
        Assert.Null(new SiteLogins(path, vault).AuthorizationFor(new Uri("https://example.com/")));
    }

    [Fact]
    public void ProxyProfileStore_RoundTrips_AndSurvivesCorruption()
    {
        var path = Path.Combine(Tmp(), "profiles.json");
        var store = new ProxyProfileStore(path);
        store.Save(new[] { new ProxyProfile("a", "NYC", ProxyKind.Socks5, "h", 1080, "u"), new ProxyProfile("b", "UK", ProxyKind.Http, "h2", 443, null) });
        var loaded = new ProxyProfileStore(path).Load();
        Assert.Equal(new[] { "a", "b" }, loaded.Select(p => p.Id));
        Assert.Equal(ProxyKind.Socks5, loaded[0].Kind);
        File.WriteAllText(path, "{{{");
        Assert.Empty(new ProxyProfileStore(path).Load());
        Assert.Empty(new ProxyProfileStore(path + ".none").Load());
    }

    // ---- antivirus hook ----
    sealed class FakeRunner : IProcessRunner
    {
        public List<(string Exe, string Args)> Calls { get; } = new();
        public int Exit { get; set; }
        public bool Throw { get; set; }
        public Task<int> RunAsync(string exe, string args, CancellationToken ct)
        {
            Calls.Add((exe, args));
            if (Throw) throw new System.ComponentModel.Win32Exception("not found");
            return Task.FromResult(Exit);
        }
    }

    [Fact]
    public async Task Antivirus_CommandRunsOnCompletionWithPathArgument()
    {
        var r = new FakeRunner();
        var res = await AntivirusRunner.RunAsync("\"C:\\Program Files\\Scan\\scan.exe\" /quick {file} /log", @"C:\Dl\a b.zip", r);
        Assert.True(res.Ran);
        Assert.Equal(0, res.ExitCode);
        var call = Assert.Single(r.Calls);
        Assert.Equal(@"C:\Program Files\Scan\scan.exe", call.Exe);
        Assert.Equal("/quick \"C:\\Dl\\a b.zip\" /log", call.Args);
    }

    [Fact]
    public async Task Antivirus_WithoutPlaceholder_AppendsQuotedPath()
    {
        var r = new FakeRunner();
        await AntivirusRunner.RunAsync("scan.exe /fast", @"C:\x.exe", r);
        Assert.Equal(("scan.exe", "/fast \"C:\\x.exe\""), r.Calls[0]);
    }

    [Fact]
    public async Task Antivirus_EmptyCommand_DoesNothing()
    {
        var r = new FakeRunner();
        Assert.False((await AntivirusRunner.RunAsync("  ", @"C:\x.exe", r)).Ran);
        Assert.False((await AntivirusRunner.RunAsync(null, @"C:\x.exe", r)).Ran);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Antivirus_NonZeroExit_AndMissingScanner_AreReported()
    {
        var bad = new FakeRunner { Exit = 2 };
        Assert.Equal(2, (await AntivirusRunner.RunAsync("scan.exe {file}", "x", bad)).ExitCode);
        var missing = new FakeRunner { Throw = true };
        var res = await AntivirusRunner.RunAsync("nope.exe {file}", "x", missing);
        Assert.True(res.Ran);
        Assert.Null(res.ExitCode);
        Assert.Contains("nope.exe", res.Error);
    }
}
