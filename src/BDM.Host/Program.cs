using BDM.Core.Ipc;
using BDM.Host;

// `bdm-host --register <extensionId> [browsers...]` / `--unregister [browsers...]` (used by the installer and tools/register-host.ps1)
if (args.Length >= 2 && args[0] == "--register")
{
    var browsers = args.Length > 2 ? args[2..] : new[] { "chrome", "edge", "brave" };
    HostRegistrar.Register(Environment.ProcessPath!, args[1], browsers);
    return;
}
if (args.Length >= 1 && args[0] == "--unregister")
{
    HostRegistrar.Unregister(args.Length > 1 ? args[1..] : new[] { "chrome", "edge", "brave" });
    return;
}

// Chrome starts this exe and talks over stdin/stdout; keep stdout clean (no Console.WriteLine).
var dir = AppContext.BaseDirectory;
var appExe = Environment.GetEnvironmentVariable("BDM_APP_EXE") ?? Path.Combine(dir, "BDM.App.exe");
var link = new PipeAppLink(PipeProtocol.PipeName, appExe, TimeSpan.FromSeconds(15));
await new HostRunner(link).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), default);
