using System;
using System.Collections.Generic;
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
                // Konsistent lokal interpretieren (wie die ISO-Werte unten und
                // File.SetLastWriteTime); sonst landen Grenzfälle im falschen Monat.
                dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                return true;
            }
        }

        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)
               || DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    /// <summary>
    /// Ersetzt Platzhalter in einer Vorlage. Unterstützt:
    /// {DocId}, {Original} (Basisname), {OriginalFull}, {Ext},
    /// {yyyy} {MM} {dd} {HH} {mm} (aus date) sowie {Feld:NAME} (Indexfeld).
    /// </summary>
    public static string ResolveTemplate(
        string template, IReadOnlyDictionary<string, string?> fields,
        DateTime? date, string docId, string originalName)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        var ext = Path.GetExtension(originalName);
        var baseName = Path.GetFileNameWithoutExtension(originalName);

        var sb = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '{')
            {
                sb.Append(template[i]);
                continue;
            }

            var end = template.IndexOf('}', i);
            if (end < 0)
            {
                sb.Append(template[i]);
                continue;
            }

            var token = template.Substring(i + 1, end - i - 1);
            i = end;

            if (token.StartsWith("Feld:", StringComparison.OrdinalIgnoreCase))
            {
                var fieldName = token.Substring(5);
                fields.TryGetValue(fieldName, out var val);
                sb.Append(val ?? "");
            }
            else
            {
                switch (token)
                {
                    case "DocId": sb.Append(docId); break;
                    case "Original": sb.Append(baseName); break;
                    case "OriginalFull": sb.Append(originalName); break;
                    case "Ext": sb.Append(ext); break;
                    case "yyyy": sb.Append(date?.ToString("yyyy", CultureInfo.InvariantCulture) ?? ""); break;
                    case "MM": sb.Append(date?.ToString("MM", CultureInfo.InvariantCulture) ?? ""); break;
                    case "dd": sb.Append(date?.ToString("dd", CultureInfo.InvariantCulture) ?? ""); break;
                    case "HH": sb.Append(date?.ToString("HH", CultureInfo.InvariantCulture) ?? ""); break;
                    case "mm": sb.Append(date?.ToString("mm", CultureInfo.InvariantCulture) ?? ""); break;
                    default: sb.Append('{').Append(token).Append('}'); break; // unbekannt: unverändert
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Baut das Zielverzeichnis aus einer Pfad-Vorlage (relativ zu outputRoot).
    /// Jedes Segment wird als Dateiname bereinigt. Leere Segmente entfallen.
    /// </summary>
    public static string ResolveTemplatedDirectory(
        string outputRoot, string pathTemplate,
        IReadOnlyDictionary<string, string?> fields, DateTime? date, string docId, string originalName)
    {
        var resolved = ResolveTemplate(pathTemplate, fields, date, docId, originalName);
        var dir = outputRoot;
        foreach (var rawSeg in resolved.Split('\\', '/'))
        {
            if (string.IsNullOrWhiteSpace(rawSeg))
                continue;
            dir = Path.Combine(dir, SanitizeFileName(rawSeg));
        }
        return dir;
    }

    /// <summary>Baut einen Dateinamen aus einer Vorlage (mit Originalname/Endung).</summary>
    public static string ResolveTemplatedFileName(
        string fileNameTemplate,
        IReadOnlyDictionary<string, string?> fields, DateTime? date, string docId,
        string originalName, int? sectionSuffix)
    {
        var resolved = ResolveTemplate(fileNameTemplate, fields, date, docId, originalName);
        var ext = Path.GetExtension(originalName);

        // Falls die Vorlage keine Endung enthält, Originalendung anhängen.
        if (!string.IsNullOrEmpty(ext) && !resolved.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            resolved += ext;

        var cleanExt = Path.GetExtension(resolved);
        var baseName = Path.GetFileNameWithoutExtension(SanitizeFileName(resolved));
        if (string.IsNullOrWhiteSpace(baseName))
            baseName = $"document_{docId}";
        if (baseName.Length > MaxBaseNameLength)
            baseName = baseName.Substring(0, MaxBaseNameLength);

        var sectionPart = sectionSuffix.HasValue ? $"_s{sectionSuffix.Value:00}" : "";
        return $"{baseName}{sectionPart}{cleanExt}";
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
