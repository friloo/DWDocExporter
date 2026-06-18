using System;
using System.Collections.Generic;
using System.Linq;
using DwDocExport;
using Xunit;

namespace DwDocExport.Tests;

/// <summary>Tests für die reine Logik der ExportEngine (Sektions-Auswahl, Filter, Zeitabschnitte).</summary>
public class ExportEngineLogicTests
{
    private static List<SectionInfo> Sections(params string[] names) =>
        names.Select((n, i) => new SectionInfo($"id{i}", n, null)).ToList();

    [Fact]
    public void ApplySectionSelection_SkipFirst_RemovesEnvelopeButKeepsSingle()
    {
        var two = Sections("mail.eml", "mail.eml");
        var skipped = ExportEngine.ApplySectionSelection(two, SectionSelection.ErsteUeberspringen);
        Assert.Single(skipped);
        Assert.Equal("id1", skipped[0].Id);

        // Bei nur einer Sektion bleibt sie erhalten (kein Datenverlust).
        var one = Sections("mail.eml");
        Assert.Single(ExportEngine.ApplySectionSelection(one, SectionSelection.ErsteUeberspringen));
    }

    [Fact]
    public void ApplySectionSelection_OnlyLast_OnlyFirst_Alle()
    {
        var three = Sections("a.eml", "b.eml", "c.eml");
        Assert.Equal("id2", Assert.Single(ExportEngine.ApplySectionSelection(three, SectionSelection.NurLetzte)).Id);
        Assert.Equal("id0", Assert.Single(ExportEngine.ApplySectionSelection(three, SectionSelection.NurErste)).Id);
        Assert.Equal(3, ExportEngine.ApplySectionSelection(three, SectionSelection.Alle).Count);
    }

    [Fact]
    public void ParseExtensionFilter_And_Matches()
    {
        var set = ExportEngine.ParseExtensionFilter("eml, msg");
        Assert.Contains("eml", set);
        Assert.Contains("msg", set);

        Assert.True(ExportEngine.MatchesExtension("Nachricht.EML", set));   // Groß/Klein egal
        Assert.True(ExportEngine.MatchesExtension("anhang.msg", set));
        Assert.False(ExportEngine.MatchesExtension("dokument.pdf", set));
        Assert.False(ExportEngine.MatchesExtension("ohneendung", set));
    }

    [Fact]
    public void ParseExtensionFilter_Empty_IsEmpty()
    {
        Assert.Empty(ExportEngine.ParseExtensionFilter(""));
        Assert.Empty(ExportEngine.ParseExtensionFilter(null));
    }

    [Fact]
    public void BuildDateRanges_Monthly_CoversRangeContiguously()
    {
        var ranges = ExportEngine.BuildDateRanges(new DateTime(2024, 1, 1), new DateTime(2024, 4, 1), DateChunking.Monatlich);
        Assert.Equal(3, ranges.Count);
        Assert.Equal(new DateTime(2024, 1, 1), ranges[0].From);
        Assert.Equal(new DateTime(2024, 2, 1), ranges[0].To);
        Assert.Equal(new DateTime(2024, 4, 1), ranges[^1].To); // bis-Grenze gekappt
    }

    [Fact]
    public void BuildDateRanges_Yearly_StartsAtYearBoundary()
    {
        var ranges = ExportEngine.BuildDateRanges(new DateTime(2022, 3, 15), new DateTime(2024, 2, 1), DateChunking.Jaehrlich);
        Assert.Equal(new DateTime(2022, 1, 1), ranges[0].From);
        Assert.Equal(new DateTime(2024, 2, 1), ranges[^1].To);
    }

    [Fact]
    public void ParseChunkStart_DefaultsAndFormats()
    {
        Assert.Equal(new DateTime(2000, 1, 1), ExportEngine.ParseChunkStart(""));
        Assert.Equal(new DateTime(2024, 6, 15), ExportEngine.ParseChunkStart("15.06.2024"));
        Assert.Equal(new DateTime(2024, 6, 15), ExportEngine.ParseChunkStart("2024-06-15"));
    }
}
