using System;
using System.Collections.Generic;
using System.IO;
using DwDocExport;
using Xunit;

namespace DwDocExport.Tests;

/// <summary>Tests für Vorlagen, Dateinamen, Zielordner und Datumsparsing in <see cref="PathRules"/>.</summary>
public class PathRulesExtraTests
{
    [Fact]
    public void BuildFileName_PrefixesDocId_AndSectionSuffix()
    {
        Assert.Equal("123_report.pdf", PathRules.BuildFileName("123", "report.pdf", null));
        Assert.Equal("123_report_s01.pdf", PathRules.BuildFileName("123", "report.pdf", 1));
    }

    [Fact]
    public void ResolveTargetDirectory_YearMonth_Unsorted_Flat()
    {
        var root = Path.Combine("C:", "Export");

        var ym = PathRules.ResolveTargetDirectory(root, true, "2024-06-15", 0, "1");
        Assert.Equal(Path.Combine(root, "2024", "06"), ym);

        var unsorted = PathRules.ResolveTargetDirectory(root, true, "kein-datum", 0, "1");
        Assert.Equal(Path.Combine(root, "_unsortiert"), unsorted);

        var flat = PathRules.ResolveTargetDirectory(root, false, null, 0, "1");
        Assert.Equal(root, flat);
    }

    [Fact]
    public void ResolveTemplate_FillsPlaceholders()
    {
        var fields = new Dictionary<string, string?> { ["NR"] = "42" };
        var result = PathRules.ResolveTemplate("{DocId}_{Feld:NR}", fields, null, "7", "x.pdf");
        Assert.Equal("7_42", result);
    }

    [Fact]
    public void TryParseDate_Iso()
    {
        Assert.True(PathRules.TryParseDate("2024-06-15", out var dt));
        Assert.Equal(2024, dt.Year);
        Assert.Equal(6, dt.Month);
        Assert.Equal(15, dt.Day);
    }

    [Fact]
    public void TryParseDate_DotNetDate_IsLocalConsistent()
    {
        var ms = new DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.True(PathRules.TryParseDate($"/Date({ms})/", out var dt));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime, dt);
    }

    [Fact]
    public void TryParseDate_Empty_False()
    {
        Assert.False(PathRules.TryParseDate("", out _));
        Assert.False(PathRules.TryParseDate(null, out _));
    }
}
