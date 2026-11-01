using System.Diagnostics;

namespace BDM.Core.Engine;

/// <summary>Seam over the OS so post-download actions can be tested without sleeping the machine.</summary>
public interface IPowerApi
{
    void Sleep();
    void Hibernate();
    void Shutdown();
    void Open(string path);
    void Start(string program, string args);
}

public sealed class WindowsPowerApi : IPowerApi
{
    public void Sleep() => Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
    public void Hibernate() => Run("shutdown.exe", "/h");
    public void Shutdown() => Run("shutdown.exe", "/s /t 30"); // 30 s grace so the owner can abort with `shutdown /a`
    public void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    public void Start(string program, string args) => Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false });
    static void Run(string exe, string args) => Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
}

public static class PostActions
{
    public static void Run(PostAction action, string? path, string? program, IPowerApi? api = null)
    {
        api ??= new WindowsPowerApi();
        switch (action)
        {
            case PostAction.OpenFile when path != null: api.Open(path); break;
            case PostAction.RunProgram when program != null: api.Start(program, path == null ? "" : $"\"{path}\""); break;
            case PostAction.Sleep: api.Sleep(); break;
            case PostAction.Hibernate: api.Hibernate(); break;
            case PostAction.Shutdown: api.Shutdown(); break;
        }
    }
}
