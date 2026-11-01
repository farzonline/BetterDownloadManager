using BDM.App.Settings;
using BDM.App.ViewModels;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.App.Tests;

public class MainViewModelTests
{
    sealed class FakeShell : IShell
    {
        public List<string> Calls { get; } = new();
        public void Open(string path) => Calls.Add("open:" + path);
        public void ShowInFolder(string path) => Calls.Add("folder:" + path);
    }

    /// <summary>Queue limit 0 keeps every download Queued, so the list can be tested without touching the network.</summary>
    static (MainViewModel Vm, DownloadManager M, FakeShell Shell, AppSettings S) Make()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None));
        var m = new DownloadManager(store, _ => new HttpClient());
        var s = new AppSettings { DownloadRoot = Path.Combine(Path.GetTempPath(), "bdm-vm-tests") };
        var shell = new FakeShell();
        return (new MainViewModel(m, s, shell, a => a()), m, shell, s);
    }

    static void Fill(MainViewModel vm)
    {
        vm.AddUrl("https://x.example/holiday.mp4");
        vm.AddUrl("https://x.example/backup.zip");
        vm.AddUrl("https://cdn.other/report.pdf?dl=1");
    }

    [Fact]
    public void AddUrl_CreatesItemInMainQueue()
    {
        var (vm, m, _, s) = Make();
        var id = vm.AddUrl("https://files.example/a/movie.mkv");
        var item = Assert.Single(vm.Items);
        Assert.Equal(id, item.Id);
        Assert.Equal("movie.mkv", item.Name);
        Assert.Equal("Video", item.Category);
        var rec = m.Get(id)!;
        Assert.Equal("main", rec.QueueId);
        Assert.Equal(Path.Combine(s.DownloadRoot, "Video"), rec.Request.DestinationDir);
    }

    [Fact]
    public void AddUrl_RejectsNonDownloadableAddresses()
    {
        var (vm, _, _, _) = Make();
        Assert.Throws<ArgumentException>(() => vm.AddUrl("not a url"));
        Assert.Throws<ArgumentException>(() => vm.AddUrl("file:///c:/windows/notepad.exe"));
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Category_Filter_ShowsOnlyMatching()
    {
        var (vm, _, _, _) = Make();
        Fill(vm);
        Assert.Equal(3, vm.Items.Count);
        vm.SelectedCategory = "Video";
        Assert.Equal(new[] { "holiday.mp4" }, vm.Items.Select(i => i.Name));
        vm.SelectedCategory = "Documents";
        Assert.Equal(new[] { "report.pdf" }, vm.Items.Select(i => i.Name));
        vm.SelectedCategory = "Finished";
        Assert.Empty(vm.Items);
        vm.SelectedCategory = "Unfinished";
        Assert.Equal(3, vm.Items.Count);
        vm.SelectedCategory = "All Downloads";
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public void Search_FiltersByNameAndUrl()
    {
        var (vm, _, _, _) = Make();
        Fill(vm);
        vm.SearchText = "HOLIDAY";
        Assert.Equal(new[] { "holiday.mp4" }, vm.Items.Select(i => i.Name));
        vm.SearchText = "cdn.other";
        Assert.Equal(new[] { "report.pdf" }, vm.Items.Select(i => i.Name));
        vm.SearchText = "";
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public void Search_And_Category_Combine()
    {
        var (vm, _, _, _) = Make();
        Fill(vm);
        vm.SelectedCategory = "Video";
        vm.SearchText = "backup";
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void StatusText_SummarisesCountsAndSelection()
    {
        var (vm, _, _, _) = Make();
        Fill(vm);
        Assert.Equal("3 items", vm.StatusText);
        vm.SetSelection(new[] { vm.Items[0], vm.Items[1] });
        Assert.Equal("3 items · 2 selected", vm.StatusText);
        vm.SearchText = "holiday";
        Assert.StartsWith("1 item", vm.StatusText);
    }

    [Fact]
    public void Pause_Resume_Remove_ActOnTheSelection()
    {
        var (vm, m, _, _) = Make();
        Fill(vm);
        var first = vm.Items[0];
        vm.SetSelection(new[] { first });

        vm.PauseCommand.Execute(null);
        Assert.Equal(DownloadState.Paused, m.Get(first.Id)!.State);
        Assert.Equal("Paused", first.StatusText);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.ResumeCommand.CanExecute(null));

        vm.ResumeCommand.Execute(null);
        Assert.Equal(DownloadState.Queued, m.Get(first.Id)!.State);

        vm.RemoveSelected(deleteFiles: false);
        Assert.Equal(2, vm.Items.Count);
        Assert.Null(m.Get(first.Id));
        Assert.Empty(vm.SelectedItems);
    }

    [Fact]
    public void Commands_AreDisabledWithoutASelection()
    {
        var (vm, _, _, _) = Make();
        Fill(vm);
        Assert.False(vm.PauseCommand.CanExecute(null));
        Assert.False(vm.ResumeCommand.CanExecute(null));
        Assert.False(vm.OpenFileCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.True(vm.AddUrlCommand.CanExecute(null));
    }

    [Fact]
    public void OpenFile_And_OpenFolder_UseTheShell_OnlyForCompletedFiles()
    {
        var (vm, m, shell, _) = Make();
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "done.zip");
        File.WriteAllText(path, "x");
        // an imported, already finished download
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "b.db"));
        var rec = new DownloadRecord(Guid.NewGuid(), new DownloadRequest(new Uri("https://x/done.zip"), null, null, new Dictionary<string, string>(), null, null, "Compressed", dir),
            DownloadState.Completed, 1, 1, null, "main", DateTime.UtcNow, FilePath: path);
        store.Upsert(rec);
        var m2 = new DownloadManager(store, _ => new HttpClient());
        var vm2 = new MainViewModel(m2, new AppSettings(), shell, a => a());

        vm2.SetSelection(new[] { vm2.Items[0] });
        Assert.True(vm2.OpenFileCommand.CanExecute(null));
        vm2.OpenFileCommand.Execute(null);
        vm2.OpenFolderCommand.Execute(null);
        Assert.Equal(new[] { "open:" + path, "folder:" + path }, shell.Calls);

        Fill(vm);
        vm.SetSelection(new[] { vm.Items[0] });
        Assert.False(vm.OpenFileCommand.CanExecute(null), "an unfinished download has no file to open");
        _ = m;
    }

    [Fact]
    public void AddUrlCommand_RaisesTheDialogRequest()
    {
        var (vm, _, _, _) = Make();
        var raised = 0;
        vm.AddUrlRequested += () => raised++;
        vm.AddUrlCommand.Execute(null);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ManagerUpdates_ReachTheListThroughThePostHook()
    {
        var store = new DownloadStore(Path.Combine(Directory.CreateTempSubdirectory().FullName, "bdm.db"));
        store.SaveQueue(new QueueDefinition("main", "Main", 0, 4, null, null, PostAction.None));
        var m = new DownloadManager(store, _ => new HttpClient());
        var posted = new List<Action>();
        var vm = new MainViewModel(m, new AppSettings(), new FakeShell(), a => posted.Add(a));
        m.Add(new DownloadRequest(new Uri("https://x/a.zip"), null, null, new Dictionary<string, string>(), null, null, "Compressed", Path.GetTempPath()));
        Assert.Empty(vm.Items); // nothing until the UI thread runs the posted work
        foreach (var a in posted.ToList()) a();
        Assert.Single(vm.Items);
    }

    // ---- formatting ----
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(5_242_880L, "5.0 MB")]
    [InlineData(3_221_225_472L, "3.0 GB")]
    public void Format_Size(long bytes, string expected) => Assert.Equal(expected, Format.Size(bytes));

    [Theory]
    [InlineData(0.0, "")]
    [InlineData(2_097_152.0, "2.0 MB/s")]
    public void Format_Speed(double bps, string expected) => Assert.Equal(expected, Format.Speed(bps));

    [Theory]
    [InlineData(5, "5s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3725, "1h 02m")]
    public void Format_Eta(int seconds, string expected) => Assert.Equal(expected, Format.Eta(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ItemViewModel_ComputesSpeedEtaAndProgressFromSamples()
    {
        var req = new DownloadRequest(new Uri("https://x/big.zip"), null, null, new Dictionary<string, string>(), null, null, "Compressed", ".");
        var rec = new DownloadRecord(Guid.NewGuid(), req, DownloadState.Running, 0, 10_000_000, null, "main", DateTime.UtcNow);
        var item = new DownloadItemViewModel(rec, DateTimeOffset.Parse("2026-11-01T10:00:00Z"));
        item.Update(rec with { Downloaded = 2_000_000 }, DateTimeOffset.Parse("2026-11-01T10:00:01Z"));
        Assert.Equal(20, item.Progress, 1);
        Assert.Equal("1.9 MB/s", item.SpeedText);
        Assert.Equal("4s", item.EtaText);
        Assert.Contains("20%", item.StatusText);
        item.Update(rec with { State = DownloadState.Completed, Downloaded = 10_000_000, FilePath = @"C:\x\big.zip" }, DateTimeOffset.Parse("2026-11-01T10:00:05Z"));
        Assert.Equal("", item.SpeedText);
        Assert.Equal(100, item.Progress);
        Assert.Equal("Complete", item.StatusText);
        Assert.Equal("big.zip", item.Name);
    }

    [Fact]
    public void ItemViewModel_StatusWords_ForFailures()
    {
        var req = new DownloadRequest(new Uri("https://x/a.zip"), null, null, new Dictionary<string, string>(), null, null, "Compressed", ".");
        DownloadRecord Rec(DownloadFailure f, string e) => new(Guid.NewGuid(), req, DownloadState.Failed, 0, null, e, "main", DateTime.UtcNow, Failure: f);
        Assert.Contains("expired", new DownloadItemViewModel(Rec(DownloadFailure.AuthExpired, "403"), DateTimeOffset.Now).StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DRM", new DownloadItemViewModel(Rec(DownloadFailure.Protected, "x"), DateTimeOffset.Now).StatusText);
        Assert.Contains("disk", new DownloadItemViewModel(Rec(DownloadFailure.DiskFull, "x"), DateTimeOffset.Now).StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Failed", new DownloadItemViewModel(Rec(DownloadFailure.Network, "boom"), DateTimeOffset.Now).StatusText);
    }
}
