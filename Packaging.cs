using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DwDocExport;

/// <summary>
/// Packt den Export in ZIP-Archive – entweder den kompletten Ausgabeordner oder
/// je Jahr/Monat ein separates Archiv. Liefert eine kurze Ergebnismeldung.
/// </summary>
public static class Packaging
{
    /// <summary>Packt den gesamten Ausgabeordner in eine ZIP-Datei.</summary>
    public static string ZipWholeOutput(string outputRoot, string zipPath)
    {
        if (!Directory.Exists(outputRoot))
            return $"Ausgabeordner nicht gefunden: {outputRoot}";

        if (File.Exists(zipPath))
            File.Delete(zipPath);

        ZipFile.CreateFromDirectory(outputRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return $"ZIP erstellt: {zipPath}";
    }

    /// <summary>
    /// Packt jede Jahr\Monat-Struktur unter dem Ausgabeordner in ein eigenes
    /// Archiv „JJJJ-MM.zip" im Zielordner. Liefert die Anzahl erstellter Archive.
    /// </summary>
    public static string ZipPerYearMonth(string outputRoot, string targetDir)
    {
        if (!Directory.Exists(outputRoot))
            return $"Ausgabeordner nicht gefunden: {outputRoot}";

        Directory.CreateDirectory(targetDir);
        var yearRx = new Regex(@"^\d{4}$");
        var monthRx = new Regex(@"^\d{2}$");
        var count = 0;

        foreach (var yearDir in Directory.GetDirectories(outputRoot))
        {
            var year = Path.GetFileName(yearDir);
            if (!yearRx.IsMatch(year))
                continue;

            foreach (var monthDir in Directory.GetDirectories(yearDir))
            {
                var month = Path.GetFileName(monthDir);
                if (!monthRx.IsMatch(month))
                    continue;

                var zipPath = Path.Combine(targetDir, $"{year}-{month}.zip");
                if (File.Exists(zipPath))
                    File.Delete(zipPath);

                ZipFile.CreateFromDirectory(monthDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
                count++;
            }
        }

        return $"{count} Monats-Archiv(e) erstellt in {targetDir}";
    }
}
