using System.Runtime.CompilerServices;

// Erlaubt dem Testprojekt den Zugriff auf interne Hilfsmethoden (z. B. die
// Sektions-Auswahl, Endungs-Filter und Zeitabschnitts-Logik der ExportEngine).
[assembly: InternalsVisibleTo("DwDocExport.Tests")]
