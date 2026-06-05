using System;
using System.Security.Cryptography;
using System.Text;

namespace DwDocExport;

/// <summary>
/// Schützt sensible Werte (Passwort, Client-Secret) in der config.json mittels
/// Windows DPAPI (<see cref="ProtectedData"/>) im Scope LocalMachine – damit kann
/// sowohl der angemeldete Administrator (GUI) als auch der als LocalSystem
/// laufende Dienst den Wert entschlüsseln.
///
/// Geschützte Werte erhalten den Präfix "DPAPI:". Werte ohne diesen Präfix gelten
/// als Klartext (z. B. handvergebene Platzhalter) und werden unverändert geliefert.
/// Auf Nicht-Windows-Plattformen (nur für Entwicklung/Tests relevant) findet keine
/// Verschlüsselung statt.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "DPAPI:";

    /// <summary>Verschlüsselt einen Klartextwert. Leere Eingabe bleibt leer.</summary>
    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
            return string.Empty;

        // Bereits geschützt? Nicht doppelt verschlüsseln.
        if (plain.StartsWith(Prefix, StringComparison.Ordinal))
            return plain;

        if (!OperatingSystem.IsWindows())
            return plain; // Entwicklungs-Fallback: kein DPAPI verfügbar.

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return Prefix + Convert.ToBase64String(enc);
        }
        catch
        {
            // Im Fehlerfall lieber Klartext als Datenverlust.
            return plain;
        }
    }

    /// <summary>Entschlüsselt einen zuvor mit <see cref="Protect"/> geschützten Wert.</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        // Kein Präfix => Klartext (Platzhalter / handvergeben).
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;

        var payload = stored.Substring(Prefix.Length);

        if (!OperatingSystem.IsWindows())
            return payload; // Kann ohne DPAPI nicht entschlüsselt werden.

        try
        {
            var data = Convert.FromBase64String(payload);
            var dec = ProtectedData.Unprotect(data, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(dec);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>True, wenn der Wert bereits DPAPI-geschützt ist.</summary>
    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}
