using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DwDocExport;

/// <summary>
/// Verwaltet mehrere Konfigurationsprofile (Jobs). Das klassische config.json
/// gilt als Profil „Standard". Weitere Profile liegen als JSON-Dateien im
/// Unterordner „profiles" neben der EXE. Der Dienst arbeitet alle Profile ab.
/// </summary>
public static class ProfileManager
{
    public const string DefaultProfileName = "Standard";

    public static string ProfilesDir => Path.Combine(AppContext.BaseDirectory, "profiles");

    /// <summary>Listet alle Profilnamen (inkl. „Standard").</summary>
    public static List<string> ListProfiles()
    {
        var names = new List<string> { DefaultProfileName };
        try
        {
            if (Directory.Exists(ProfilesDir))
                foreach (var f in Directory.GetFiles(ProfilesDir, "*.json"))
                    names.Add(Path.GetFileNameWithoutExtension(f));
        }
        catch { }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Liefert den Dateipfad zu einem Profil.</summary>
    public static string PathFor(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName) ||
            profileName.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            return ExporterOptions.DefaultPath;

        var safe = SanitizeProfileName(profileName);
        return Path.Combine(ProfilesDir, safe + ".json");
    }

    public static ExporterOptions Load(string profileName) => ExporterOptions.Load(PathFor(profileName));

    public static void Save(ExporterOptions opt, string profileName)
    {
        var path = PathFor(profileName);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        opt.Save(path);
    }

    public static void Delete(string profileName)
    {
        if (profileName.Equals(DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            return; // Standard nicht löschen
        var path = PathFor(profileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>Alle Konfigurationsdateien (für den Dienstlauf über alle Profile).</summary>
    public static List<string> GetAllConfigPaths()
    {
        var paths = new List<string>();
        if (File.Exists(ExporterOptions.DefaultPath))
            paths.Add(ExporterOptions.DefaultPath);
        try
        {
            if (Directory.Exists(ProfilesDir))
                paths.AddRange(Directory.GetFiles(ProfilesDir, "*.json"));
        }
        catch { }
        return paths;
    }

    private static string SanitizeProfileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }
}
