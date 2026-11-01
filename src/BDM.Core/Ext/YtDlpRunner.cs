using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BDM.Core.Engine;
using BDM.Core.Streams;

namespace BDM.Core.Ext;

public class YtDlpException(string message) : Exception(message);

public class YtDlpMissingException(string message) : Exception(message);

public record YtDlpInfo(string Title, IReadOnlyList<StreamChoice> Choices, bool IsLive);

/// <summary>Runs yt-dlp; split out so tests can fake the process.</summary>
public interface IYtDlpProcess
{
    Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct);
}

public sealed class YtDlpProcess(string exe) : IYtDlpProcess
{
    public async Task<(int Exit, string StdOut, string StdErr)> RunAsync(IReadOnlyList<string> args, Action<string>? onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi) ?? throw new YtDlpMissingException("yt-dlp did not start."); }
        catch (Win32Exception) { throw new YtDlpMissingException($"yt-dlp could not be started from {exe}. Check its path in Options > Tools."); }
        using (p)
        {
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data == null) return; stdout.AppendLine(e.Data); onStdoutLine?.Invoke(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
            p.WaitForExit(); // flush the async readers
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}

/// <summary>Downloads from sites BDM can't read directly (video pages) by delegating to yt-dlp.</summary>
public sealed class YtDlpRunner(Func<string?> exePath, Func<string, IYtDlpProcess>? processFor = null, Func<string?>? ffmpegPath = null)
{
    IYtDlpProcess Process_()
    {
        var exe = exePath();
        if (string.IsNullOrWhiteSpace(exe))
            throw new YtDlpMissingException("yt-dlp was not found. Download yt-dlp.exe and set its path in Options > Tools, then try again.");
        return (processFor ?? (e => new YtDlpProcess(e)))(exe);
    }

    static void AddHeaders(List<string> args, IReadOnlyDictionary<string, string>? headers)
    {
        foreach (var (k, v) in headers ?? new Dictionary<string, string>()) { args.Add("--add-header"); args.Add($"{k}: {v}"); }
    }

    static string ErrorLine(string stderr, int exit)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var err = lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
        return err != null ? err["ERROR:".Length..].Trim() : lines.LastOrDefault() ?? $"yt-dlp failed (exit code {exit}).";
    }

    public async Task<YtDlpInfo> InfoAsync(Uri page, IReadOnlyDictionary<string, string>? headers, CancellationToken ct, string? proxyUrl = null)
    {
        var proc = Process_();
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings" };
        if (!string.IsNullOrEmpty(proxyUrl)) { args.Add("--proxy"); args.Add(proxyUrl); }
        AddHeaders(args, headers);
        args.Add(page.ToString());
        var (exit, stdout, stderr) = await proc.RunAsync(args, null, ct);
        if (exit != 0) throw new YtDlpException(ErrorLine(stderr, exit));

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array)
                throw new YtDlpException("yt-dlp found nothing to download on that page.");
            var title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "video" : "video";
            var live = root.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True;

            static string Codec(JsonElement f, string name) => f.TryGetProperty(name, out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "none";
            var heights = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
            var hasAudioOnly = false;
            foreach (var f in formats.EnumerateArray())
            {
                var v = Codec(f, "vcodec");
                if (v != "none" && f.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.GetInt32() > 0) heights.Add(h.GetInt32());
                else if (v == "none" && Codec(f, "acodec") != "none") hasAudioOnly = true;
            }

            StreamChoice Choice(string label, string selector) => new(label, new StreamTrack(selector, page), null, []);
            var choices = heights.Select(h => Choice($"{h}p", $"bv*[height<={h}]+ba/b[height<={h}]")).ToList();
            if (hasAudioOnly) choices.Add(Choice("Audio only", "ba/b"));
            if (choices.Count == 0) choices.Add(Choice("Best available", "b"));
            return new YtDlpInfo(title, choices, live);
        }
        catch (JsonException e) { throw new YtDlpException("yt-dlp gave an answer BDM couldn't read: " + e.Message); }
    }

    /// <returns>Path of the finished file.</returns>
    public async Task<string> DownloadAsync(Uri page, string formatSelector, string destDir, IReadOnlyDictionary<string, string>? headers,
        IProgress<DownloadProgress>? progress, CancellationToken ct, string? proxyUrl = null)
    {
        var proc = Process_();
        Directory.CreateDirectory(destDir);
        var args = new List<string>
        {
            "-f", formatSelector, "--no-playlist", "--newline", "--no-colors", "--progress", "--merge-output-format", "mp4",
            "-o", Path.Combine(destDir, "%(title).150B.%(ext)s"),
            "--progress-template", "download:BDMPROGRESS %(progress.downloaded_bytes)s %(progress.total_bytes)s %(progress.total_bytes_estimate)s",
            "--print", "after_move:BDMFILE %(filepath)s",
        };
        if (ffmpegPath?.Invoke() is { Length: > 0 } ff) { args.Add("--ffmpeg-location"); args.Add(ff); }
        if (!string.IsNullOrEmpty(proxyUrl)) { args.Add("--proxy"); args.Add(proxyUrl); }
        AddHeaders(args, headers);
        args.Add(page.ToString());

        string? file = null;
        void OnLine(string line)
        {
            if (line.StartsWith("BDMFILE ", StringComparison.Ordinal)) file = line["BDMFILE ".Length..].Trim();
            else if (line.StartsWith("BDMPROGRESS ", StringComparison.Ordinal) && progress != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                long? Num(int i) => parts.Length > i && long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
                    : parts.Length > i && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : null;
                if (Num(1) is { } done) progress.Report(new DownloadProgress(done, Num(2) ?? Num(3), 0, []));
            }
        }

        var (exit, _, stderr) = await proc.RunAsync(args, OnLine, ct);
        if (exit != 0) throw new YtDlpException(ErrorLine(stderr, exit));
        return file ?? throw new YtDlpException("yt-dlp finished but did not report the file it saved.");
    }
}
