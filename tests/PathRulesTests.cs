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
