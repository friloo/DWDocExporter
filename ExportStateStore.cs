using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace DwDocExport;

/// <summary>Ein abgeschlossener Eintrag (für Verifikation/Manifest).</summary>
public sealed record DoneEntry(string DocId, string? SavedPath, string? Sha256, string? Fields);

/// <summary>
/// Persistenter Zustandsspeicher (SQLite mit WAL). Verhindert Doppel-Downloads,
/// ermöglicht Wiederaufnahme, speichert Prüfsummen und Lauf-Metadaten.
/// Tabelle Documents(DocId PK, Status, SavedPath, Fields, Sha256, Error, UpdatedUtc).
/// </summary>
public sealed class ExportStateStore : IDisposable
{
    public const string StatusDone = "done";
    public const string StatusError = "error";

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public ExportStateStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _conn = new SqliteConnection(csb.ToString());
        _conn.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS Documents(
                    DocId      TEXT PRIMARY KEY,
                    Status     TEXT NOT NULL,
                    SavedPath  TEXT,
                    Fields     TEXT,
                    Sha256     TEXT,
                    Error      TEXT,
                    UpdatedUtc TEXT NOT NULL
                );");
        Exec(@"CREATE TABLE IF NOT EXISTS Meta(Key TEXT PRIMARY KEY, Value TEXT);");

        // Migration: Sha256-Spalte für ältere DBs nachrüsten.
        TryExec("ALTER TABLE Documents ADD COLUMN Sha256 TEXT;");
    }

    public DateTime? GetLastRunUtc()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Meta WHERE Key='LastRunUtc' LIMIT 1;";
            var v = cmd.ExecuteScalar() as string;
            if (v != null && DateTime.TryParse(v, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
            return null;
        }
    }

    public void SetLastRunUtc(DateTime utc)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO Meta(Key, Value) VALUES('LastRunUtc', $v)
                                ON CONFLICT(Key) DO UPDATE SET Value=$v;";
            cmd.Parameters.AddWithValue("$v", utc.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    public bool IsDone(string docId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Status FROM Documents WHERE DocId=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", docId);
            return cmd.ExecuteScalar() as string == StatusDone;
        }
    }

    public void MarkDone(string docId, string savedPath, string fieldsJson, string? sha256 = null)
        => Upsert(docId, StatusDone, savedPath, fieldsJson, sha256, null);

    public void MarkError(string docId, string error, string? fieldsJson = null)
        => Upsert(docId, StatusError, null, fieldsJson, null, error);

    public int CountDone() => CountByStatus(StatusDone);
    public int CountError() => CountByStatus(StatusError);

    private int CountByStatus(string status)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", status);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public string GetLastErrors(int max = 5)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT DocId, Error FROM Documents WHERE Status=$s ORDER BY UpdatedUtc DESC LIMIT $m;";
            cmd.Parameters.AddWithValue("$s", StatusError);
            cmd.Parameters.AddWithValue("$m", max);

            var sb = new StringBuilder();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                sb.AppendLine($"  Doc {reader.GetString(0)}: {(reader.IsDBNull(1) ? "" : reader.GetString(1))}");
            return sb.ToString();
        }
    }

    /// <summary>Liefert die DocIds aller fehlerhaften Dokumente (für erneuten Versuch).</summary>
    public List<string> GetErrorDocIds()
    {
        lock (_lock)
        {
            var ids = new List<string>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DocId FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", StatusError);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                ids.Add(reader.GetString(0));
            return ids;
        }
    }

    /// <summary>Entfernt alle Fehler-Einträge, damit sie erneut versucht werden.</summary>
    public int ClearErrors()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", StatusError);
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Liefert alle erfolgreich exportierten Einträge (für Verifikation/Manifest).</summary>
    public List<DoneEntry> GetDone()
    {
        lock (_lock)
        {
            var list = new List<DoneEntry>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DocId, SavedPath, Sha256, Fields FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", StatusDone);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new DoneEntry(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            return list;
        }
    }

    /// <summary>Schreibt ein CSV-Manifest aller exportierten Dokumente.</summary>
    public void ExportManifestCsv(string csvPath)
    {
        var entries = GetDone();
        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var w = new StreamWriter(csvPath, false, Encoding.UTF8);
        w.WriteLine("DocId;SavedPath;Sha256");
        foreach (var e in entries)
            w.WriteLine($"{Csv(e.DocId)};{Csv(e.SavedPath)};{Csv(e.Sha256)}");
    }

    private static string Csv(string? v)
    {
        v ??= "";
        if (v.Contains(';') || v.Contains('"') || v.Contains('\n'))
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    private void Upsert(string docId, string status, string? savedPath, string? fieldsJson, string? sha, string? error)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Documents(DocId, Status, SavedPath, Fields, Sha256, Error, UpdatedUtc)
                VALUES($id, $st, $sp, $fl, $sh, $er, $ts)
                ON CONFLICT(DocId) DO UPDATE SET
                    Status=$st, SavedPath=$sp, Fields=$fl, Sha256=$sh, Error=$er, UpdatedUtc=$ts;";
            cmd.Parameters.AddWithValue("$id", docId);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$sp", (object?)savedPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fl", (object?)fieldsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sh", (object?)sha ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$er", (object?)error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void TryExec(string sql)
    {
        try { Exec(sql); } catch { /* Spalte existiert bereits */ }
    }

    public void Dispose()
    {
        _conn.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
