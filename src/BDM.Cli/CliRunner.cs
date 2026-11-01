using System.Net.Http.Json;
using System.Text.Json;
using BDM.Core.Api;

namespace BDM.Cli;

/// <summary>`bdm add|list|status|pause|resume` against the running app's local API. Exit codes: 0 ok, 1 usage/input, 2 app not running, 3 cannot connect, 4 token rejected.</summary>
public static class CliRunner
{
    const string Usage = """
        Usage:
          bdm add <url>          queue a download
          bdm list [--json]      show all downloads
          bdm status <id>        show one download
          bdm pause <id>         pause a download
          bdm resume <id>        resume a download
        <id> can be the first characters of the id shown by `bdm list`.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, string dataDir, HttpMessageHandler? handler = null)
    {
        var cmd = args.FirstOrDefault()?.ToLowerInvariant();
        var rest = args.Skip(1).ToList();
        var valid = cmd switch
        {
            "add" => rest.Count == 1,
            "list" => rest.All(a => a == "--json"),
            "status" or "pause" or "resume" => rest.Count == 1,
            _ => false,
        };
        if (!valid) { (cmd == null || cmd is "help" or "--help" or "-h" ? output : error).WriteLine(Usage); return cmd is "help" or "--help" or "-h" ? 0 : 1; }
        if (cmd == "add" && (!Uri.TryCreate(rest[0], UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "ftp" or "ftps")))
        {
            error.WriteLine("That doesn't look like a web address. Use the full address, for example https://site.example/file.zip");
            return 1;
        }

        var info = ApiInfo.Read(dataDir);
        if (info == null) { error.WriteLine("Better Download Manager isn't running. Start it, then try again."); return 2; }

        using var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri($"http://127.0.0.1:{info.Port}/");
        http.DefaultRequestHeaders.Add("X-BDM-Token", info.Token);
        http.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            switch (cmd)
            {
                case "add":
                {
                    var resp = await http.PostAsJsonAsync("add", new { url = rest[0] });
                    if (await Check(resp, error) is { } fail) return fail;
                    var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
                    output.WriteLine($"Added {id}");
                    return 0;
                }
                case "list":
                {
                    var resp = await http.GetAsync("downloads");
                    if (await Check(resp, error) is { } fail) return fail;
                    var json = await resp.Content.ReadAsStringAsync();
                    if (rest.Contains("--json")) { output.WriteLine(json); return 0; }
                    var rows = JsonSerializer.Deserialize<List<JsonElement>>(json)!;
                    if (rows.Count == 0) { output.WriteLine("No downloads."); return 0; }
                    foreach (var r in rows) output.WriteLine(Line(r));
                    return 0;
                }
                default:
                {
                    var list = await http.GetAsync("downloads");
                    if (await Check(list, error) is { } fail) return fail;
                    var rows = (await list.Content.ReadFromJsonAsync<List<JsonElement>>())!;
                    var matches = rows.Where(r => r.GetProperty("id").GetString()!.StartsWith(rest[0], StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count == 0) { error.WriteLine($"No download matches '{rest[0]}'. Run `bdm list` to see the ids."); return 1; }
                    if (matches.Count > 1) { error.WriteLine($"'{rest[0]}' matches {matches.Count} downloads. Type more of the id."); return 1; }
                    var id = matches[0].GetProperty("id").GetString()!;
                    if (cmd == "status")
                    {
                        var m = matches[0];
                        output.WriteLine(Line(m));
                        output.WriteLine("  " + m.GetProperty("url").GetString());
                        if (m.GetProperty("error").ValueKind == JsonValueKind.String) output.WriteLine("  Error: " + m.GetProperty("error").GetString());
                        return 0;
                    }
                    var resp = await http.PostAsync($"downloads/{id}/{cmd}", null);
                    if (await Check(resp, error) is { } f2) return f2;
                    output.WriteLine(cmd == "pause" ? $"Paused {id[..8]}" : $"Resumed {id[..8]}");
                    return 0;
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            error.WriteLine("Better Download Manager isn't running (couldn't reach it). Start it, then try again.");
            return 3;
        }
    }

    static string Line(JsonElement r)
    {
        var progress = r.GetProperty("progress").ValueKind == JsonValueKind.Number ? $"{r.GetProperty("progress").GetDouble():0}%" : "";
        return $"{r.GetProperty("id").GetString()![..8]}  {r.GetProperty("state").GetString(),-10} {progress,5}  {r.GetProperty("name").GetString()}";
    }

    /// <returns>An exit code when the response is an error, else null.</returns>
    static async Task<int?> Check(HttpResponseMessage resp, TextWriter error)
    {
        if (resp.IsSuccessStatusCode) return null;
        if ((int)resp.StatusCode == 401) { error.WriteLine("The app didn't accept this tool's token. Restart Better Download Manager and try again."); return 4; }
        string? message = null;
        try { message = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(); } catch (Exception) { /* no body */ }
        error.WriteLine(message ?? $"The app answered {(int)resp.StatusCode}.");
        return 1;
    }
}
