using System;
using System.IO;
using DwDocExport;
using Xunit;

namespace DwDocExport.Tests;

/// <summary>Unit-Tests für die reinen Pfad-/Namenslogik in <see cref="PathRules"/>.</summary>
public class PathRulesTests
{
    [Fact]
    public void SanitizeFileName_ReplacesInvalidChars()
    {
        // Eingabe aus den tatsächlich ungültigen Zeichen der Plattform bilden,
        // damit der Test auf Windows wie auf anderen Systemen aussagekräftig ist.
        var invalid = Path.GetInvalidFileNameChars();
        var input = "datei" + new string(invalid) + "name.txt";

        var result = PathRules.SanitizeFileName(input);

        foreach (var c in invalid)
            Assert.DoesNotContain(c, result);
    }

    [Fact]
    public void SanitizeFileName_EmptyBecomesUnderscore()
    {
        Assert.Equal("_", PathRules.SanitizeFileName(""));
        Assert.Equal("_", PathRules.SanitizeFileName(null));
    }

    [Fact]
    public void BuildFileName_PrefixesDocId()
    {
        var name = PathRules.BuildFileName("123", "mail.msg", null);
        Assert.Equal("123_mail.msg", name);
    }

    [Fact]
    public void BuildFileName_AddsSectionSuffix()
    {
        var name = PathRules.BuildFileName("9", "scan.pdf", 0);
        Assert.Equal("9_scan_s00.pdf", name);
    }

    [Fact]
    public void BuildFileName_LimitsBaseLength()
    {
        var longName = new string('x', 300) + ".pdf";
        var name = PathRules.BuildFileName("1", longName, null);
        var baseLen = Path.GetFileNameWithoutExtension(name).Length;
        // "1_" Präfix + max. 120 Zeichen Basis
        Assert.True(baseLen <= 2 + PathRules.MaxBaseNameLength);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public void ResolveTargetDirectory_FlatWhenNoDateField()
    {
        var dir = PathRules.ResolveTargetDirectory(@"C:\Out", false, null, 0, "1");
        Assert.Equal(@"C:\Out", dir);
    }

    [Fact]
    public void ResolveTargetDirectory_YearMonthWhenDateParsable()
    {
        var dir = PathRules.ResolveTargetDirectory(@"C:\Out", true, "2023-07-15T00:00:00", 0, "1");
        Assert.Contains(Path.Combine("2023", "07"), dir);
    }

    [Fact]
    public void ResolveTargetDirectory_UnsortedWhenDateNotParsable()
    {
        var dir = PathRules.ResolveTargetDirectory(@"C:\Out", true, "kein-datum", 0, "1");
        Assert.Contains("_unsortiert", dir);
    }

    [Fact]
    public void ResolveTargetDirectory_AddsHashSubfolders()
    {
        var dir = PathRules.ResolveTargetDirectory(@"C:\Out", false, null, 2, "doc-42");
        var hash = PathRules.StableHashHex("doc-42");
        Assert.Contains(Path.Combine(hash[0].ToString(), hash[1].ToString()), dir);
    }

    [Theory]
    [InlineData("/Date(1689379200000)/", true)]
    [InlineData("2023-07-15", true)]
    [InlineData("", false)]
    [InlineData("nonsense", false)]
    public void TryParseDate_HandlesFormats(string raw, bool expected)
    {
        Assert.Equal(expected, PathRules.TryParseDate(raw, out _));
    }

    [Fact]
    public void StableHashHex_IsDeterministic()
    {
        Assert.Equal(PathRules.StableHashHex("abc"), PathRules.StableHashHex("abc"));
        Assert.NotEqual(PathRules.StableHashHex("abc"), PathRules.StableHashHex("abd"));
    }
}

/// <summary>Tests für Vorlagen (Pfad/Dateiname).</summary>
public class TemplateTests
{
    private static Dictionary<string, string?> Fields() => new()
    {
        ["KUNDE"] = "Müller GmbH",
        ["BELEGNR"] = "4711"
    };

    [Fact]
    public void ResolveTemplate_ReplacesFieldAndDate()
    {
        var dt = new DateTime(2023, 7, 15);
        var s = PathRules.ResolveTemplate("{Feld:KUNDE}/{yyyy}/{MM}/{DocId}", Fields(), dt, "99", "x.pdf");
        Assert.Equal("Müller GmbH/2023/07/99", s);
    }

    [Fact]
    public void ResolveTemplatedDirectory_SanitizesSegments()
    {
        var dt = new DateTime(2023, 1, 2);
        var dir = PathRules.ResolveTemplatedDirectory(@"C:\Out", @"{Feld:KUNDE}\{yyyy}", Fields(), dt, "1", "x.pdf");
        Assert.Contains("2023", dir);
        Assert.Contains("Out", dir);
    }

    [Fact]
    public void ResolveTemplatedFileName_AppendsExtensionWhenMissing()
    {
        var name = PathRules.ResolveTemplatedFileName("{DocId}_{Feld:BELEGNR}", Fields(), null, "7", "scan.pdf", null);
        Assert.Equal("7_4711.pdf", name);
    }

    [Fact]
    public void ResolveTemplatedFileName_KeepsExtensionAndSection()
    {
        var name = PathRules.ResolveTemplatedFileName("{Original}", Fields(), null, "7", "mail.msg", 1);
        Assert.Equal("mail_s01.msg", name);
    }
}

/// <summary>Tests für die clientseitige Filterlogik.</summary>
public class DocumentFilterTests
{
    private static Dictionary<string, string?> Fields() => new()
    {
        ["STATUS"] = "offen",
        ["DATUM"] = "2023-06-15"
    };

    [Fact]
    public void Matches_NoFilters_ReturnsTrue()
    {
        Assert.True(DocumentFilter.Matches(Fields(), new ExporterOptions()));
    }

    [Fact]
    public void Matches_FieldEquals()
    {
        var opt = new ExporterOptions();
        opt.FieldConditions.Add(new FieldCondition { Field = "STATUS", Operator = FilterOperator.Equals, Value = "offen" });
        Assert.True(DocumentFilter.Matches(Fields(), opt));

        opt.FieldConditions[0].Value = "erledigt";
        Assert.False(DocumentFilter.Matches(Fields(), opt));
    }

    [Fact]
    public void Matches_DateRange()
    {
        var opt = new ExporterOptions { FilterDateField = "DATUM", FilterDateFrom = "2023-06-01", FilterDateTo = "2023-06-30" };
        Assert.True(DocumentFilter.Matches(Fields(), opt));

        opt.FilterDateFrom = "2023-07-01";
        Assert.False(DocumentFilter.Matches(Fields(), opt));
    }
}

/// <summary>Tests für die DPAPI-Geheimnis-Behandlung (Klartext-Fälle, plattformneutral).</summary>
public class SecretProtectorTests
{
    [Fact]
    public void Unprotect_PlainStaysPlain()
    {
        Assert.Equal("hello", SecretProtector.Unprotect("hello"));
    }

    [Fact]
    public void Protect_EmptyStaysEmpty()
    {
        Assert.Equal(string.Empty, SecretProtector.Protect(""));
        Assert.Equal(string.Empty, SecretProtector.Protect(null));
    }

    [Fact]
    public void ProtectUnprotect_Roundtrip()
    {
        // Auf Windows verschlüsselt/entschlüsselt DPAPI; sonst Klartext-Fallback.
        var roundtrip = SecretProtector.Unprotect(SecretProtector.Protect("geheim123"));
        Assert.Equal("geheim123", roundtrip);
    }
}
