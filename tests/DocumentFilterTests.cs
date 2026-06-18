using System.Collections.Generic;
using DwDocExport;
using Xunit;

namespace DwDocExport.Tests;

/// <summary>Tests für die clientseitige Filterlogik (<see cref="DocumentFilter"/>).</summary>
public class DocumentFilterTests
{
    private static Dictionary<string, string?> Fields(params (string, string?)[] kv)
    {
        var d = new Dictionary<string, string?>();
        foreach (var (k, v) in kv) d[k] = v;
        return d;
    }

    [Fact]
    public void NoFilter_AlwaysMatches()
    {
        var opt = new ExporterOptions();
        Assert.True(DocumentFilter.Matches(Fields(("X", "y")), opt));
    }

    [Fact]
    public void DateRange_InsideMatches_OutsideDoesNot()
    {
        var opt = new ExporterOptions
        {
            FilterDateField = "DATE",
            FilterDateFrom = "2024-06-01",
            FilterDateTo = "2024-06-30"
        };

        Assert.True(DocumentFilter.Matches(Fields(("DATE", "2024-06-15")), opt));
        Assert.False(DocumentFilter.Matches(Fields(("DATE", "2024-07-01")), opt));
        Assert.False(DocumentFilter.Matches(Fields(("DATE", "2024-05-31")), opt));
    }

    [Fact]
    public void DateRange_MissingValue_DoesNotMatch()
    {
        var opt = new ExporterOptions { FilterDateField = "DATE", FilterDateFrom = "2024-01-01" };
        Assert.False(DocumentFilter.Matches(Fields(("OTHER", "x")), opt));
    }

    [Theory]
    [InlineData(FilterOperator.Equals, "Acme", "Acme", true)]
    [InlineData(FilterOperator.Equals, "Acme", "Other", false)]
    [InlineData(FilterOperator.NotEquals, "Acme", "Other", true)]
    [InlineData(FilterOperator.Contains, "Acme GmbH", "GmbH", true)]
    [InlineData(FilterOperator.StartsWith, "Acme GmbH", "Acme", true)]
    [InlineData(FilterOperator.StartsWith, "Acme GmbH", "GmbH", false)]
    public void FieldCondition_IsEvaluated(FilterOperator op, string actual, string expected, bool result)
    {
        var opt = new ExporterOptions();
        opt.FieldConditions.Add(new FieldCondition { Field = "COMPANY", Operator = op, Value = expected });
        Assert.Equal(result, DocumentFilter.Matches(Fields(("COMPANY", actual)), opt));
    }

    [Fact]
    public void MultipleConditions_AreAndCombined()
    {
        var opt = new ExporterOptions();
        opt.FieldConditions.Add(new FieldCondition { Field = "A", Operator = FilterOperator.Equals, Value = "1" });
        opt.FieldConditions.Add(new FieldCondition { Field = "B", Operator = FilterOperator.Equals, Value = "2" });

        Assert.True(DocumentFilter.Matches(Fields(("A", "1"), ("B", "2")), opt));
        Assert.False(DocumentFilter.Matches(Fields(("A", "1"), ("B", "9")), opt));
    }
}
