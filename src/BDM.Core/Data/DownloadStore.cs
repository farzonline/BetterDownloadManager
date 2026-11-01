using System.Text.Json;
using System.Text.Json.Serialization;
using BDM.Core.Engine;
using Microsoft.Data.Sqlite;

namespace BDM.Core.Data;

/// <summary>SQLite persistence: downloads and queues are stored as JSON with a few searchable columns.</summary>
public sealed class DownloadStore : IDisposable
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    readonly SqliteConnection _db;
    readonly object _gate = new();

    public DownloadStore(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _db = new SqliteConnection($"Data Source={dbPath}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec("CREATE TABLE IF NOT EXISTS downloads(id TEXT PRIMARY KEY, json TEXT NOT NULL, search TEXT NOT NULL, added TEXT NOT NULL)");
        Exec("CREATE TABLE IF NOT EXISTS queues(id TEXT PRIMARY KEY, json TEXT NOT NULL)");
        if (Queue("main") == null) SaveQueue(QueueDefinition.Main);
    }

    void Exec(string sql, params (string, object)[] args)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
            c.ExecuteNonQuery();
        }
    }

    List<string> Query(string sql, params (string, object)[] args)
    {
        lock (_gate)
        {
            using var c = _db.CreateCommand();
            c.CommandText = sql;
            foreach (var (k, v) in args) c.Parameters.AddWithValue(k, v);
            using var r = c.ExecuteReader();
            var rows = new List<string>();
            while (r.Read()) rows.Add(r.GetString(0));
            return rows;
        }
    }

    static string SearchText(DownloadRecord r)
    {
        var name = r.FilePath != null ? Path.GetFileName(r.FilePath) : r.Request.FileName ?? Path.GetFileName(r.Request.Url.AbsolutePath);
        return $"{name} {r.Request.Url}";
    }

    public void Upsert(DownloadRecord r) => Exec(
        "INSERT INTO downloads(id,json,search,added) VALUES($id,$j,$s,$a) ON CONFLICT(id) DO UPDATE SET json=$j, search=$s",
        ("$id", r.Id.ToString()), ("$j", JsonSerializer.Serialize(r, Json)), ("$s", SearchText(r)), ("$a", r.Added.ToString("O")));

    public void Delete(Guid id) => Exec("DELETE FROM downloads WHERE id=$id", ("$id", id.ToString()));

    public DownloadRecord? Get(Guid id) =>
        Query("SELECT json FROM downloads WHERE id=$id", ("$id", id.ToString())).Select(Read).FirstOrDefault();

    public IReadOnlyList<DownloadRecord> All() => Query("SELECT json FROM downloads ORDER BY added").Select(Read).ToList();

    public IReadOnlyList<DownloadRecord> Search(string text) =>
        string.IsNullOrEmpty(text)
            ? All()
            : Query("SELECT json FROM downloads WHERE instr(lower(search), lower($t)) > 0 ORDER BY added DESC", ("$t", text)).Select(Read).ToList();

    static DownloadRecord Read(string json) => JsonSerializer.Deserialize<DownloadRecord>(json, Json)!;

    public IReadOnlyList<QueueDefinition> Queues() =>
        Query("SELECT json FROM queues ORDER BY id").Select(j => JsonSerializer.Deserialize<QueueDefinition>(j, Json)!).ToList();

    public QueueDefinition? Queue(string id) =>
        Query("SELECT json FROM queues WHERE id=$id", ("$id", id)).Select(j => JsonSerializer.Deserialize<QueueDefinition>(j, Json)!).FirstOrDefault();

    public void SaveQueue(QueueDefinition q) => Exec(
        "INSERT INTO queues(id,json) VALUES($id,$j) ON CONFLICT(id) DO UPDATE SET json=$j",
        ("$id", q.Id), ("$j", JsonSerializer.Serialize(q, Json)));

    record Dump(List<QueueDefinition> Queues, List<DownloadRecord> Downloads);

    public string Export() => JsonSerializer.Serialize(new Dump(Queues().ToList(), All().ToList()), Json);

    public void Import(string json)
    {
        var d = JsonSerializer.Deserialize<Dump>(json, Json) ?? throw new FormatException("Not a BDM export.");
        foreach (var q in d.Queues) SaveQueue(q);
        foreach (var r in d.Downloads) Upsert(r);
    }

    public void Dispose() { lock (_gate) _db.Dispose(); }
}
