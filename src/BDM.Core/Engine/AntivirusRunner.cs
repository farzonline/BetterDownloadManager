using System.ComponentModel;
using System.Diagnostics;

namespace BDM.Core.Engine;

public interface IProcessRunner
{
    /// <returns>The exit code.</returns>
    Task<int> RunAsync(string exe, string args, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string exe, string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("The process did not start.");
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }
}

public record AvResult(bool Ran, int? ExitCode, string? Error = null);

/// <summary>Runs the owner's scanner on a finished file. The command may contain {file}; otherwise the quoted path is appended.</summary>
public static class AntivirusRunner
{
    public static async Task<AvResult> RunAsync(string? command, string path, IProcessRunner? runner = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command)) return new AvResult(false, null);
        command = command.Trim();
        string exe, rest;
        if (command.StartsWith('"'))
        {
            var close = command.IndexOf('"', 1);
            if (close < 0) return new AvResult(false, null, "The scanner command has an unclosed quote.");
            exe = command[1..close];
            rest = command[(close + 1)..].Trim();
        }
        else
        {
            var space = command.IndexOf(' ');
            (exe, rest) = space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
        }
        var quoted = $"\"{path}\"";
        var args = rest.Contains("{file}") ? rest.Replace("{file}", quoted) : (rest.Length == 0 ? quoted : rest + " " + quoted);
        try
        {
            return new AvResult(true, await (runner ?? new ProcessRunner()).RunAsync(exe, args, ct));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return new AvResult(true, null, $"Could not run {exe}: {e.Message}");
        }
    }
}
