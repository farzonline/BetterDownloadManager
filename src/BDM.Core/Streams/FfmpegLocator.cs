using System.Diagnostics;
using System.Text;

namespace BDM.Core.Streams;

/// <summary>Finds ffmpeg: the configured path, then next to the app, then on PATH.</summary>
public sealed class FfmpegLocator(string? configuredPath = null, Func<string?, string?>? find = null)
{
    public string? Locate() => (find ?? Find)(configuredPath);

    public static string? Find(string? configured) => FindExe("ffmpeg", configured);

    /// <summary>ffprobe sits next to ffmpeg in every standard build.</summary>
    public static string? FindProbe(string? ffmpegPath)
    {
        var sibling = ffmpegPath == null ? null : Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        return sibling != null && File.Exists(sibling) ? sibling : FindExe("ffprobe", null);
    }

    static string? FindExe(string name, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var beside = Path.Combine(AppContext.BaseDirectory, name + ".exe");
        if (File.Exists(beside)) return beside;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }
}

public static class FfmpegRunner
{
    public static async Task RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-nostdin" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var err = new StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        _ = p.StandardOutput.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch (InvalidOperationException) { } throw; }
        if (p.ExitCode != 0) throw new IOException($"ffmpeg failed (exit {p.ExitCode}): {err.ToString().Trim()}");
    }
}
