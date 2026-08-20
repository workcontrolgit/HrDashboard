using FluentAssertions;
using HrDashboard.Agents.Models;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class HrMetricParserTests
{
    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        HrMetricParser.Parse(string.Empty).Should().BeEmpty();
        HrMetricParser.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoJsonArray_ReturnsEmpty()
    {
        var result = HrMetricParser.Parse("The average salary is high.");
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ValidJsonArray_ReturnsRows()
    {
        const string text = """[{"label":"IT","value":8000.0,"category":"AvgSalary"}]""";

        var result = HrMetricParser.Parse(text);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("IT");
        result[0].Value.Should().Be(8000.0);
        result[0].Category.Should().Be("AvgSalary");
    }

    [Fact]
    public void Parse_JsonEmbeddedInText_ExtractsRows()
    {
        const string text = """
            Here is the data:
            [{"label":"HR","value":50,"category":"Headcount"}]
            That is all.
            """;

        var result = HrMetricParser.Parse(text);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("HR");
        result[0].Value.Should().Be(50.0);
        result[0].Category.Should().Be("Headcount");
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmptyWithoutThrowing()
    {
        var result = HrMetricParser.Parse("[not valid json{{");
        result.Should().BeEmpty();
    }
}
