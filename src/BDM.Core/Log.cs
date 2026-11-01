namespace BDM.Core;

/// <summary>Rolling plain-text log: one file per day (bdm-yyyy-MM-dd.log), files older than a week are deleted. A no-op until configured; never throws.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _dir;

    public static void Configure(string? directory)
    {
        lock (Gate)
        {
            _dir = directory;
            if (directory == null) return;
            try
            {
                Directory.CreateDirectory(directory);
                foreach (var f in Directory.GetFiles(directory, "bdm-*.log"))
                    if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* logging must never stop the app */ }
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? error = null) => Write("ERROR", message, error);

    static void Write(string level, string message, Exception? error)
    {
        lock (Gate)
        {
            if (_dir == null) return;
            try
            {
                var now = DateTime.Now;
                File.AppendAllText(Path.Combine(_dir, $"bdm-{now:yyyy-MM-dd}.log"),
                    $"{now:HH:mm:ss.fff} {level} {message}{(error == null ? "" : Environment.NewLine + error)}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* disk full or file locked: drop the line */ }
        }
    }
}
