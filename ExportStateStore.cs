using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace DwDocExport;

/// <summary>
/// Persistenter Zustandsspeicher (SQLite mit WAL). Verhindert Doppel-Downloads
/// und ermöglicht ein fortsetzbares Wiederaufnehmen des Exports.
/// Tabelle: Documents(DocId PK, Status, SavedPath, Fields, Error, UpdatedUtc).
/// </summary>
public sealed class ExportStateStore : IDisposable
{
    public const string StatusPending = "pending";
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

        // WAL-Modus für robusten, parallelen Lesezugriff (z. B. GUI-Fortschritt).
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec(@"CREATE TABLE IF NOT EXISTS Documents(
                    DocId      TEXT PRIMARY KEY,
                    Status     TEXT NOT NULL,
                    SavedPath  TEXT,
                    Fields     TEXT,
                    Error      TEXT,
                    UpdatedUtc TEXT NOT NULL
                );");
    }

    /// <summary>Prüft, ob ein Dokument bereits erfolgreich exportiert wurde.</summary>
    public bool IsDone(string docId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Status FROM Documents WHERE DocId=$id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", docId);
            var result = cmd.ExecuteScalar() as string;
            return string.Equals(result, StatusDone, StringComparison.Ordinal);
        }
    }

    /// <summary>Markiert ein Dokument als erfolgreich exportiert.</summary>
    public void MarkDone(string docId, string savedPath, string fieldsJson)
    {
        Upsert(docId, StatusDone, savedPath, fieldsJson, null);
    }

    /// <summary>Markiert ein Dokument als fehlerhaft (Fehlertext wird gespeichert).</summary>
    public void MarkError(string docId, string error, string? fieldsJson = null)
    {
        Upsert(docId, StatusError, null, fieldsJson, error);
    }

    /// <summary>Anzahl erfolgreich exportierter Dokumente (Fortschrittsanzeige).</summary>
    public int CountDone()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", StatusDone);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Anzahl fehlerhafter Dokumente.</summary>
    public int CountError()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Documents WHERE Status=$s;";
            cmd.Parameters.AddWithValue("$s", StatusError);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    /// <summary>Liefert die letzten Fehlermeldungen (für die GUI-Anzeige).</summary>
    public string GetLastErrors(int max = 5)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT DocId, Error FROM Documents WHERE Status=$s ORDER BY UpdatedUtc DESC LIMIT $m;";
            cmd.Parameters.AddWithValue("$s", StatusError);
            cmd.Parameters.AddWithValue("$m", max);

            var sb = new System.Text.StringBuilder();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var docId = reader.GetString(0);
                var err = reader.IsDBNull(1) ? "" : reader.GetString(1);
                sb.AppendLine($"  Doc {docId}: {err}");
            }
            return sb.ToString();
        }
    }

    private void Upsert(string docId, string status, string? savedPath, string? fieldsJson, string? error)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO Documents(DocId, Status, SavedPath, Fields, Error, UpdatedUtc)
                VALUES($id, $st, $sp, $fl, $er, $ts)
                ON CONFLICT(DocId) DO UPDATE SET
                    Status=$st, SavedPath=$sp, Fields=$fl, Error=$er, UpdatedUtc=$ts;";
            cmd.Parameters.AddWithValue("$id", docId);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$sp", (object?)savedPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fl", (object?)fieldsJson ?? DBNull.Value);
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

    public void Dispose()
    {
        _conn.Dispose();
        // Verbindungs-Pool leeren, damit WAL-Dateien freigegeben werden.
        SqliteConnection.ClearAllPools();
    }
}
