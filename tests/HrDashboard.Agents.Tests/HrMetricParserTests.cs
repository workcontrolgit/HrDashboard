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

    [Fact]
    public void TryParse_GenuineEmptyArray_ReturnsTrueWithEmptyList()
    {
        // A model correctly answering "no rows match" (e.g. "departments with more than
        // 5 employees" against a 5-employee dataset) emits an empty array per the system
        // prompt's contract — this must be distinguishable from "no array present at all".
        const string text = "[]\nNo departments have more than 5 employees.";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_NoJsonArray_ReturnsFalse()
    {
        var found = HrMetricParser.TryParse("Just a conversational answer with no data.", out var metrics);

        found.Should().BeFalse();
        metrics.Should().BeEmpty();
    }

    [Fact]
    public void TryParse_ValidJsonArrayWithRows_ReturnsTrueWithRows()
    {
        const string text = """[{"label":"IT","value":8000.0,"category":"AvgSalary"}]""";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics.Should().HaveCount(1);
    }

    [Fact]
    public void TryParse_InvalidJson_ReturnsFalse()
    {
        var found = HrMetricParser.TryParse("[not valid json{{", out var metrics);

        found.Should().BeFalse();
        metrics.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ToolCallScaffoldingBeforePayload_ExtractsRealArray()
    {
        // Reproduces a local-LLM (Ollama) quirk: the model echoes back the raw tool-call
        // announcement and OllamaSharp's serialized FunctionResultContent (which itself embeds
        // a nested JSON array under "content") before finally emitting the intended payload.
        // Naive first-'['-to-last-']' slicing would previously span both blobs and fail to parse.
        const string text = """
            {"name": "Employee salary data by department", "arguments": {}}

            {"CallId":"7a1b2c3d","Result":{"content":[{"type":"text","text":"Department: HR | Avg Salary: 9500.0"}],"isError":false}}

            [{"label":"HR","value":9500.0,"category":"AvgSalary"}]
            The average salary for HR is $9,500.
            """;

        var result = HrMetricParser.Parse(text);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("HR");
        result[0].Value.Should().Be(9500.0);
    }

    [Fact]
    public void StripScaffolding_ToolCallJsonBeforePayload_RemovesPreamble()
    {
        const string text = """
            {"name": "Employee salary data by department", "arguments": {}}

            {"CallId":"7a1b2c3d","Result":{"content":[{"type":"text","text":"..."}],"isError":false}}

            [{"label":"HR","value":9500.0,"category":"AvgSalary"}]
            The average salary for HR is $9,500.
            """;

        var result = HrMetricParser.StripScaffolding(text);

        result.Should().StartWith("""[{"label":"HR","value":9500.0,"category":"AvgSalary"}]""");
        result.Should().NotContain("CallId");
        result.Should().NotContain("arguments");
    }

    [Fact]
    public void StripScaffolding_NoMetricsArray_ReturnsTextUnchanged()
    {
        const string text = "Just a conversational answer with no data.";

        HrMetricParser.StripScaffolding(text).Should().Be(text);
    }

    [Fact]
    public void ExtractDisplayText_ArrayFollowedBySummary_ReturnsOnlySummary()
    {
        const string text = """
            [{"label":"Administration","value":19333.33,"category":"AvgSalary","headcount":3},{"label":"IT","value":7500.0,"category":"AvgSalary","headcount":2}]
            Administration has an average salary of $19,333.33 (3 employees) and IT has an average salary of $7,500.00 (2 employees).
            """;

        var result = HrMetricParser.ExtractDisplayText(text);

        result.Should().Be("Administration has an average salary of $19,333.33 (3 employees) and IT has an average salary of $7,500.00 (2 employees).");
    }

    [Fact]
    public void ExtractDisplayText_ScaffoldingThenArrayThenSummary_ReturnsOnlySummary()
    {
        const string text = """
            {"name": "Employee salary data by department", "arguments": {}}

            {"CallId":"7a1b2c3d","Result":{"content":[{"type":"text","text":"..."}],"isError":false}}

            [{"label":"HR","value":9500.0,"category":"AvgSalary"}]
            The average salary for HR is $9,500.
            """;

        var result = HrMetricParser.ExtractDisplayText(text);

        result.Should().Be("The average salary for HR is $9,500.");
    }

    [Fact]
    public void ExtractDisplayText_NoMetricsArray_ReturnsTextUnchanged()
    {
        const string text = "Just a conversational answer with no data.";

        HrMetricParser.ExtractDisplayText(text).Should().Be(text);
    }
}
