using System.Text.Json;
using BDM.App.Settings;
using BDM.Core.Data;
using BDM.Core.Engine;
using BDM.Core.Net;
using Microsoft.Win32;

namespace BDM.App.Services;

/// <summary>Pushes the user's settings into the running engine; called at startup and after the options window saves.</summary>
public static class SettingsApplier
{
    /// <param name="antivirus">Runs (command, file path) after a download completes; null disables the hook.</param>
    public static void Apply(AppSettings s, DownloadManager manager, DownloadStore store, Func<string, string, Task>? antivirus)
    {
        manager.GlobalProxyId = string.IsNullOrWhiteSpace(s.GlobalProxyId) ? null : s.GlobalProxyId;
        manager.CategoryProxies.Clear();
        foreach (var (category, id) in s.CategoryProxies) if (!string.IsNullOrWhiteSpace(id)) manager.CategoryProxies[category] = id;

        manager.GlobalLimiter ??= new SpeedLimiter(0);
        manager.GlobalLimiter.SetLimit(Math.Max(0, s.SpeedLimitKBps) * 1024L);

        if (store.Queue("main") is { } main && main.Connections != s.Connections)
            store.SaveQueue(main with { Connections = Math.Clamp(s.Connections, 1, 32) });

        var command = s.AntivirusCommand;
        manager.OnCompleted = antivirus == null || string.IsNullOrWhiteSpace(command)
            ? null
            : rec => rec.FilePath is { } path ? antivirus(command, path) : Task.CompletedTask;
    }
}

/// <summary>"Start with Windows": a per-user Run key value that launches the app hidden in the tray.</summary>
public static class StartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "BetterDownloadManager";

    public static void Set(bool enabled, string exePath, string subKey = RunKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --background");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static bool IsEnabled(string subKey = RunKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(ValueName) != null;
    }
}

/// <summary>Everything the owner would want to carry to another PC: settings, proxy profiles, queues, downloads, and which sites had logins (never passwords).</summary>
public static class SettingsBundle
{
    const string FormatId = "bdm-settings-v1";

    public record ImportResult(string[] LoginHosts);

    record Bundle(string Format, AppSettings Settings, List<ProxyProfile> Profiles, List<SiteLogin> Logins, string Data);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static string Export(AppSettings settings, IEnumerable<ProxyProfile> profiles, IEnumerable<SiteLogin> logins, DownloadStore store) =>
        JsonSerializer.Serialize(new Bundle(FormatId, settings, profiles.ToList(), logins.ToList(), store.Export()), Json);

    public static ImportResult Import(string json, AppSettings target, ProxyProfileStore profiles, DownloadStore store)
    {
        Bundle bundle;
        try { bundle = JsonSerializer.Deserialize<Bundle>(json, Json) ?? throw new FormatException("This file is empty."); }
        catch (JsonException e) { throw new FormatException("This is not a BDM settings file: " + e.Message, e); }
        if (bundle.Format != FormatId || bundle.Settings == null) throw new FormatException("This is not a BDM settings file.");

        target.CopyFrom(bundle.Settings);
        profiles.Save(bundle.Profiles ?? new());
        if (!string.IsNullOrEmpty(bundle.Data)) store.Import(bundle.Data);
        return new ImportResult((bundle.Logins ?? new()).Select(l => l.Host).ToArray());
    }
}
