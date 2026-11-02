using BDM.App.Services;
using BDM.Core.Data;
using BDM.Core.Engine;

namespace BDM.App.ViewModels;

/// <summary>One row of the download list: a DownloadRecord turned into display text, with speed and ETA from successive samples.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    DateTimeOffset _sampleTime;
    long _sampleBytes;
    double _speed;
    string _name = "", _statusText = "", _sizeText = "", _speedText = "", _etaText = "", _stateKind = "queued";
    double _progress;
    DownloadState _state;

    public DownloadItemViewModel(DownloadRecord record, DateTimeOffset now)
    {
        Id = record.Id;
        Added = record.Added;
        Url = record.Request.Url.ToString();
        _sampleTime = now;
        _sampleBytes = record.Downloaded;
        Apply(record);
    }

    public Guid Id { get; }
    public DateTime Added { get; }
    public string Url { get; }
    public string Category { get; private set; } = "Other";
    /// <summary>Icon and tile colour of the file type, for the row's leading tile.</summary>
    public string CategoryGlyph => Categories.Glyph(Category);
    public string CategoryHue => Categories.Hue(Category);
    /// <summary>queued / running / paused / done / failed / protected: what the row is coloured and iconed by (never colour alone).</summary>
    public string StateKind { get => _stateKind; private set { if (Set(ref _stateKind, value)) Raise(nameof(StateGlyph)); } }
    public string StateGlyph => StateKind switch
    {
        "running" => "",
        "paused" => "",
        "done" => "",
        "protected" => "",
        "failed" => "",
        _ => "",
    };
    public string? FilePath { get; private set; }
    public bool IsStream { get; private set; }
    public string AddedText => Added.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string? Description { get; private set; }
    /// <summary>Size for sorting the Size column.</summary>
    public long SizeBytes { get; private set; }

    public string Name { get => _name; private set => Set(ref _name, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    /// <summary>0–100, or -1 when the total size is unknown.</summary>
    public double Progress { get => _progress; private set { if (Set(ref _progress, value)) Raise(nameof(PercentText)); } }
    /// <summary>"42%", or a dash while the total size is unknown.</summary>
    public string PercentText => Progress < 0 ? "—" : $"{Math.Floor(Progress):0}%";
    public DownloadState State { get => _state; private set { if (Set(ref _state, value)) { Raise(nameof(CanPause)); Raise(nameof(CanResume)); Raise(nameof(HasFile)); } } }

    public bool CanPause => State is DownloadState.Queued or DownloadState.Running;
    public bool CanResume => State is DownloadState.Paused or DownloadState.Failed;
    public bool HasFile => State == DownloadState.Completed && FilePath != null;

    public void Update(DownloadRecord r, DateTimeOffset now)
    {
        if (r.State == DownloadState.Running)
        {
            var dt = (now - _sampleTime).TotalSeconds;
            if (dt >= 0.2)
            {
                var instant = Math.Max(0, (r.Downloaded - _sampleBytes) / dt);
                _speed = _speed > 0 ? 0.7 * _speed + 0.3 * instant : instant;
                if (_speed < 1) _speed = 0; // nothing is arriving: the average would otherwise shrink forever without reaching 0
                _sampleTime = now;
                _sampleBytes = r.Downloaded;
            }
        }
        else
        {
            _speed = 0;
            _sampleTime = now;
            _sampleBytes = r.Downloaded;
        }
        Apply(r);
    }

    void Apply(DownloadRecord r)
    {
        FilePath = r.FilePath;
        Description = r.Request.Description;
        IsStream = r.Stream != null;
        Name = r.FilePath != null ? Path.GetFileName(r.FilePath)
            : r.Request.FileName ?? (r.Stream?.Title is { Length: > 0 } t ? t + ".mp4" : Categories.NameFromUrl(r.Request.Url));
        var category = r.Stream != null ? "Video" : r.Request.Category ?? Categories.For(Name);
        if (category != Category) { Category = category; Raise(nameof(Category)); Raise(nameof(CategoryGlyph)); Raise(nameof(CategoryHue)); }
        State = r.State;
        StateKind = r.State switch
        {
            DownloadState.Running => "running",
            DownloadState.Paused => "paused",
            DownloadState.Completed => "done",
            DownloadState.Failed => r.Failure == DownloadFailure.Protected ? "protected" : "failed",
            _ => "queued",
        };

        var progress = r.State == DownloadState.Completed ? 100
            : r.Total is > 0 ? Math.Min(100, r.Downloaded * 100.0 / r.Total.Value)
            : r.State == DownloadState.Running ? -1 : 0;
        Progress = progress;
        SizeBytes = r.Total ?? r.Downloaded;
        SizeText = r.Total is > 0 ? Format.Size(r.Total.Value) : r.Downloaded > 0 ? Format.Size(r.Downloaded) : "";
        SpeedText = r.State == DownloadState.Running ? Format.Speed(_speed) : "";
        EtaText = r.State == DownloadState.Running && _speed > 0 && r.Total is > 0
            ? Format.Eta((r.Total.Value - r.Downloaded) / _speed) : "";
        StatusText = r.State switch
        {
            DownloadState.Queued => "Queued",
            DownloadState.Running => progress >= 0 ? $"Downloading {Math.Floor(progress):0}%" : "Downloading",
            DownloadState.Paused => "Paused",
            DownloadState.Completed => "Complete",
            _ => r.Failure switch
            {
                DownloadFailure.AuthExpired => "Access refused — reopen the page and retry",
                DownloadFailure.Protected => "Protected (DRM) — can't be downloaded",
                DownloadFailure.DiskFull => "Disk full — free some space, then resume",
                _ => "Failed — " + (r.Error ?? "unknown error"),
            },
        };
    }
}
