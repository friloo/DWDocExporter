using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace DwDocExport;

/// <summary>
/// Threadsicheres Datei-Log mit Mindest-Loglevel und automatischer Rotation.
/// Wird von Dienst und GUI gemeinsam genutzt.
/// </summary>
public static class FileLog
{
    private static readonly object Sync = new();
    private static string? _path;
    private static LogLevel _minLevel = LogLevel.Information;
    private static long _maxBytes = 10L * 1024 * 1024;

    /// <summary>Konfiguriert Pfad, Mindestlevel und maximale Dateigröße (MB) vor Rotation.</summary>
    public static void Configure(string? path, LogLevel minLevel = LogLevel.Information, int maxSizeMb = 10)
    {
        lock (Sync)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            _minLevel = minLevel;
            _maxBytes = Math.Max(1, maxSizeMb) * 1024L * 1024L;
            if (_path != null)
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                }
                catch { }
            }
        }
    }

    public static LogLevel MinLevel { get { lock (Sync) { return _minLevel; } } }

    public static string? Path { get { lock (Sync) { return _path; } } }

    /// <summary>Wandelt einen Loglevel-Text (z. B. "Warning") in einen LogLevel.</summary>
    public static LogLevel ParseLevel(string? text) =>
        Enum.TryParse<LogLevel>(text, true, out var lvl) ? lvl : LogLevel.Information;

    public static void Write(string message)
    {
        string? path;
        lock (Sync) { path = _path; }
        if (path == null)
            return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                RotateIfNeeded(path);
                File.AppendAllText(path, line);
            }
            catch { }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length >= _maxBytes)
            {
                var backup = path + ".1";
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(path, backup);
            }
        }
        catch { }
    }
}

/// <summary>ILogger-Provider, der Einträge des Dienstes in die <see cref="FileLog"/> schreibt.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLoggerImpl();
    public void Dispose() { }

    private sealed class FileLoggerImpl : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= FileLog.MinLevel && logLevel != LogLevel.None;

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
