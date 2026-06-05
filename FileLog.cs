using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Einfaches, threadsicheres Datei-Log. Wird sowohl vom Dienst (zusätzlich zum
/// Windows-Ereignisprotokoll) als auch von der GUI genutzt, sodass beide in
/// dieselbe Logdatei schreiben.
/// </summary>
public static class FileLog
{
    private static readonly object Sync = new();
    private static string? _path;

    /// <summary>Setzt den Pfad der Logdatei (Verzeichnis wird bei Bedarf erstellt).</summary>
    public static void Configure(string? path)
    {
        lock (Sync)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            if (_path != null)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch { /* Logging darf nie den Ablauf stören. */ }
            }
        }
    }

    /// <summary>Aktueller Logdateipfad (oder null, wenn nicht konfiguriert).</summary>
    public static string? Path
    {
        get { lock (Sync) { return _path; } }
    }

    /// <summary>Schreibt eine Zeile mit Zeitstempel in die Logdatei.</summary>
    public static void Write(string message)
    {
        string? path;
        lock (Sync) { path = _path; }
        if (path == null)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (Sync)
        {
            try { File.AppendAllText(path, line); }
            catch { /* Logging darf nie den Ablauf stören. */ }
        }
    }
}

/// <summary>ILogger-Provider, der Log-Einträge des Dienstes in die <see cref="FileLog"/> schreibt.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLoggerImpl(categoryName);

    public void Dispose() { }

    private sealed class FileLoggerImpl : ILogger
    {
        private readonly string _category;
        public FileLoggerImpl(string category) => _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            var msg = formatter(state, exception);
            if (exception != null)
                msg += " | " + exception.Message;

            FileLog.Write($"[{logLevel}] {msg}");
        }
    }
}
