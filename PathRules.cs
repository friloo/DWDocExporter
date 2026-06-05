using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DwDocExport;

/// <summary>
/// Reine (testbare) Hilfsfunktionen für Ziel-Pfade und Dateinamen. Ohne
/// Seiteneffekte, damit sie sich in Unit-Tests prüfen lassen.
/// </summary>
public static class PathRules
{
    /// <summary>Maximale Länge des Basis-Dateinamens (ohne Präfix/Endung).</summary>
    public const int MaxBaseNameLength = 120;

    /// <summary>Ersetzt für Dateinamen ungültige Zeichen durch Unterstriche.</summary>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);

        var result = sb.ToString().Trim();
        return result.Length == 0 ? "_" : result;
    }

    /// <summary>
    /// Baut den Zieldateinamen "{DocId}_{Originalname}" mit bereinigten Zeichen
    /// und begrenzter Länge. Bei Sektionen wird ein Suffix "_sNN" eingefügt.
    /// </summary>
    public static string BuildFileName(string docId, string originalName, int? sectionSuffix)
    {
        var cleanOriginal = SanitizeFileName(originalName);
        var ext = Path.GetExtension(cleanOriginal);
        var baseName = Path.GetFileNameWithoutExtension(cleanOriginal);

        if (string.IsNullOrWhiteSpace(baseName))
            baseName = "document";

        if (baseName.Length > MaxBaseNameLength)
            baseName = baseName.Substring(0, MaxBaseNameLength);

        var sectionPart = sectionSuffix.HasValue ? $"_s{sectionSuffix.Value:00}" : "";
        var prefix = $"{SanitizeFileName(docId)}_";

        return $"{prefix}{baseName}{sectionPart}{ext}";
    }

    /// <summary>
    /// Ermittelt das Zielverzeichnis: bei gesetztem und parsebarem Datumswert
    /// OutputRoot\JJJJ\MM, bei gesetztem aber nicht parsebarem Feld
    /// OutputRoot\_unsortiert, sonst OutputRoot. Optional Hash-Unterordner.
    /// </summary>
    public static string ResolveTargetDirectory(
        string outputRoot, bool dateFieldConfigured, string? dateRawValue,
        int folderHashDepth, string docId)
    {
        var dir = outputRoot;

        if (dateFieldConfigured)
        {
            if (TryParseDate(dateRawValue, out var dt))
            {
                dir = Path.Combine(outputRoot,
                    dt.ToString("yyyy", CultureInfo.InvariantCulture),
                    dt.ToString("MM", CultureInfo.InvariantCulture));
            }
            else
            {
                dir = Path.Combine(outputRoot, "_unsortiert");
            }
        }

        if (folderHashDepth > 0)
        {
            var hash = StableHashHex(docId);
            var depth = Math.Min(folderHashDepth, hash.Length);
            for (var i = 0; i < depth; i++)
                dir = Path.Combine(dir, hash[i].ToString());
        }

        return dir;
    }

    /// <summary>Versucht, einen DocuWare-Datumswert in verschiedenen Formaten zu parsen.</summary>
    public static bool TryParseDate(string? raw, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        // DocuWare liefert Datumswerte häufig als ISO-8601 oder /Date(…)/.
        if (raw.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var inner = raw.Substring(6).TrimEnd(')', '/');
            var sign = inner.IndexOfAny(new[] { '+', '-' }, 1);
            if (sign > 0)
                inner = inner.Substring(0, sign);
            if (long.TryParse(inner, out var ms))
            {
                dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                return true;
            }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)
               || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    /// <summary>Stabiler, deterministischer Hex-Hash (für Hash-Unterordner).</summary>
    public static string StableHashHex(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Liefert für absolute Windows-Pfade die erweiterte Schreibweise mit
    /// "\\?\"-Präfix, damit Pfade länger als MAX_PATH (260) funktionieren.
    /// Auf anderen Plattformen oder bei relativen Pfaden bleibt der Pfad unverändert.
    /// </summary>
    public static string ToExtendedLengthPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !OperatingSystem.IsWindows())
            return path;

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;

        // UNC-Pfade: \\server\share -> \\?\UNC\server\share
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);

        // Klassischer absoluter Pfad C:\...
        if (Path.IsPathRooted(path) && path.Length >= 2 && path[1] == ':')
            return @"\\?\" + path;

        return path;
    }
}
