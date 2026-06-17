using System;
using System.Collections.Generic;

namespace DwDocExport;

/// <summary>
/// Wertet clientseitig aus, ob ein Dokument den konfigurierten Filtern entspricht
/// (Datumsbereich auf einem Indexfeld sowie beliebige Feld-Bedingungen).
/// Reine Logik – ohne Seiteneffekte, damit testbar.
/// </summary>
public static class DocumentFilter
{
    /// <summary>True, wenn das Dokument exportiert werden soll.</summary>
    public static bool Matches(IReadOnlyDictionary<string, string?> fields, ExporterOptions opt)
    {
        // Datumsbereich
        if (!string.IsNullOrWhiteSpace(opt.FilterDateField) &&
            (HasValue(opt.FilterDateFrom) || HasValue(opt.FilterDateTo)))
        {
            fields.TryGetValue(opt.FilterDateField, out var raw);
            if (!PathRules.TryParseDate(raw, out var dt))
                return false; // kein/kein parsebares Datum -> aus dem Bereich

            if (HasValue(opt.FilterDateFrom) && PathRules.TryParseDate(opt.FilterDateFrom, out var from)
                && dt.Date < from.Date)
                return false;
            if (HasValue(opt.FilterDateTo) && PathRules.TryParseDate(opt.FilterDateTo, out var to)
                && dt.Date > to.Date)
                return false;
        }

        // Feld-Bedingungen (alle müssen zutreffen = UND-Verknüpfung)
        if (opt.FieldConditions != null)
        {
            foreach (var cond in opt.FieldConditions)
            {
                if (string.IsNullOrWhiteSpace(cond.Field))
                    continue;
                fields.TryGetValue(cond.Field, out var actual);
                if (!ConditionHolds(actual ?? "", cond.Operator, cond.Value ?? ""))
                    return false;
            }
        }

        return true;
    }

    private static bool ConditionHolds(string actual, FilterOperator op, string expected)
    {
        switch (op)
        {
            case FilterOperator.Equals:
                return string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
            case FilterOperator.NotEquals:
                return !string.Equals(actual.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
            case FilterOperator.Contains:
                return actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
            case FilterOperator.StartsWith:
                return actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase);
            default:
                return true;
        }
    }

    private static bool HasValue(string? s) => !string.IsNullOrWhiteSpace(s);
}
