using System.Collections.ObjectModel;
using BDM.App.Services;
using BDM.App.Settings;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Http;

namespace BDM.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    readonly DownloadManager _manager;
    readonly AppSettings _settings;
    readonly IShell _shell;
    readonly Action<Action> _post;
    readonly List<DownloadItemViewModel> _all = new();
    List<DownloadItemViewModel> _selection = new();
    string _selectedCategory = Services.Categories.All, _searchText = "";

    /// <param name="post">Runs work on the UI thread (the manager raises events on background threads).</param>
    public MainViewModel(DownloadManager manager, AppSettings settings, IShell shell, Action<Action>? post = null)
    {
        _manager = manager;
        _settings = settings;
        _shell = shell;
        _post = post ?? (a => a());

        var now = DateTimeOffset.UtcNow;
        foreach (var r in manager.All()) _all.Add(new DownloadItemViewModel(r, now));

        AddUrlCommand = new RelayCommand(() => AddUrlRequested?.Invoke());
        PauseCommand = new RelayCommand(() => ForSelection(i => i.CanPause, _manager.Pause), () => _selection.Any(i => i.CanPause));
        ResumeCommand = new RelayCommand(() => ForSelection(i => i.CanResume, _manager.Resume), () => _selection.Any(i => i.CanResume));
        RemoveCommand = new RelayCommand(() => RemoveRequested?.Invoke(), () => _selection.Count > 0);
        OpenFileCommand = new RelayCommand(() => _shell.Open(_selection[0].FilePath!), () => _selection is [{ HasFile: true }]);
        OpenFolderCommand = new RelayCommand(() => _shell.ShowInFolder(_selection[0].FilePath!), () => _selection is [{ HasFile: true }]);
        StopAllCommand = new RelayCommand(() => ForAll(i => i.CanPause, _manager.Pause), () => _all.Any(i => i.CanPause));
        StartQueueCommand = new RelayCommand(() => ForAll(i => i.CanResume && i.StateKind != "protected", _manager.Resume),
            () => _all.Any(i => i.CanResume && i.StateKind != "protected"));
        DeleteCompletedCommand = new RelayCommand(DeleteCompleted, () => _all.Any(i => i.State == DownloadState.Completed));

        _manager.Changed += r => _post(() => Apply(r));
        Sync();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public IReadOnlyList<string> Categories => Services.Categories.Sidebar;
    public IReadOnlyList<DownloadItemViewModel> SelectedItems => _selection;

    public RelayCommand AddUrlCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResumeCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFileCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand StopAllCommand { get; }
    /// <summary>Resumes every paused or failed download (not DRM-protected ones).</summary>
    public RelayCommand StartQueueCommand { get; }
    /// <summary>Clears finished rows from the list; the files stay.</summary>
    public RelayCommand DeleteCompletedCommand { get; }

    /// <summary>The view shows its add dialog / delete confirmation in response to these.</summary>
    public event Action? AddUrlRequested;
    public event Action? RemoveRequested;

    public string SelectedCategory { get => _selectedCategory; set { if (Set(ref _selectedCategory, value)) Sync(); } }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value ?? "")) Sync(); } }

    public string StatusText
    {
        get
        {
            var n = Items.Count;
            var text = $"{n} {(n == 1 ? "item" : "items")}";
            return _selection.Count > 0 ? $"{text} · {_selection.Count} selected" : text;
        }
    }

    /// <param name="proxyProfileId">Route just this download through that proxy profile; blank = no override.</param>
    public Guid AddUrl(string url, string? proxyProfileId = null)
    {
        var uri = DownloadPlan.LinkFrom(url) ?? throw new ArgumentException("That is not a downloadable address.", nameof(url));
        return Add(DownloadPlan.Propose(uri, _settings) with { ProxyProfileId = proxyProfileId });
    }

    /// <summary>Queues what the download dialog settled; "Download later" leaves it paused.</summary>
    public Guid Add(DownloadPlan plan)
    {
        var id = _manager.Add(plan.ApplyTo(new DownloadRequest(plan.Url, null, null, new Dictionary<string, string>(), null, null, null, plan.Folder)));
        if (!plan.StartNow) _manager.Pause(id);
        return id;
    }

    public void SetSelection(IEnumerable<DownloadItemViewModel> selected)
    {
        _selection = selected.Where(Items.Contains).ToList();
        Raise(nameof(SelectedItems));
        Raise(nameof(StatusText));
        RaiseCommands();
    }

    public void RemoveSelected(bool deleteFiles)
    {
        foreach (var item in _selection.ToList())
        {
            _manager.Remove(item.Id, deleteFiles);
            _all.Remove(item);
        }
        _selection = new();
        Sync();
        Raise(nameof(SelectedItems));
    }

    void ForSelection(Func<DownloadItemViewModel, bool> where, Action<Guid> act)
    {
        foreach (var i in _selection.Where(where).ToList()) act(i.Id);
    }

    void ForAll(Func<DownloadItemViewModel, bool> where, Action<Guid> act)
    {
        foreach (var i in _all.Where(where).ToList()) act(i.Id);
    }

    void DeleteCompleted()
    {
        foreach (var i in _all.Where(i => i.State == DownloadState.Completed).ToList())
        {
            _manager.Remove(i.Id, deleteFile: false);
            _all.Remove(i);
        }
        Sync();
    }

    void Apply(DownloadRecord r)
    {
        var item = _all.FirstOrDefault(i => i.Id == r.Id);
        if (item == null) _all.Add(new DownloadItemViewModel(r, DateTimeOffset.UtcNow));
        else item.Update(r, DateTimeOffset.UtcNow);
        Sync();
    }

    bool Matches(DownloadItemViewModel i)
    {
        var inCategory = _selectedCategory switch
        {
            Services.Categories.All => true,
            Services.Categories.Unfinished => i.State != DownloadState.Completed,
            Services.Categories.Finished => i.State == DownloadState.Completed,
            var c => i.Category == c,
        };
        return inCategory && (_searchText.Length == 0 ||
            i.Name.Contains(_searchText, StringComparison.OrdinalIgnoreCase) || i.Url.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Brings Items in line with the filters with the fewest changes, so the view keeps its selection and scroll position.</summary>
    void Sync()
    {
        var desired = _all.Where(Matches).OrderBy(i => i.Added).ToList();
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!desired.Contains(Items[i])) Items.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i], desired[i])) continue;
            var existing = Items.IndexOf(desired[i]);
            if (existing >= 0) Items.Move(existing, i); else Items.Insert(i, desired[i]);
        }
        _selection = _selection.Where(Items.Contains).ToList();
        Raise(nameof(StatusText));
        RaiseCommands();
    }

    void RaiseCommands()
    {
        PauseCommand.RaiseCanExecuteChanged();
        ResumeCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        OpenFileCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        StopAllCommand.RaiseCanExecuteChanged();
        StartQueueCommand.RaiseCanExecuteChanged();
        DeleteCompletedCommand.RaiseCanExecuteChanged();
    }
}
