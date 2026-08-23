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
    public void Parse_RowWithoutChartableField_DefaultsToTrue()
    {
        // Every existing curated tool's output predates the "chartable" field entirely —
        // it must still parse as chartable so none of them need to change.
        const string text = """[{"label":"IT","value":8000.0,"category":"AvgSalary"}]""";

        var result = HrMetricParser.Parse(text);

        result[0].Chartable.Should().BeTrue();
    }

    [Fact]
    public void Parse_RowWithChartableFalse_ParsesFlag()
    {
        const string text = """[{"label":"Steven King","value":24000.0,"category":"Administration","chartable":false}]""";

        var result = HrMetricParser.Parse(text);

        result[0].Chartable.Should().BeFalse();
    }

    [Fact]
    public void Parse_CategoryAsJsonNumber_CoercesToString()
    {
        // Live failure (2026-08-23): asking "list employees" made the model emit
        // "category":10 (the raw department ID) instead of a quoted string once the
        // categoryName field started encouraging it to think of category as "Department ID".
        // Category is typed string? — strict System.Text.Json deserialization throws on a
        // number token for a string property, which previously failed TryParse for the
        // *entire* array (not just that field), silently discarding a perfectly good listing.
        const string text = """[{"label":"Steven King","value":24000.0,"category":10,"chartable":false}]""";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics.Should().HaveCount(1);
        metrics[0].Category.Should().Be("10");
    }

    [Fact]
    public void Parse_ValueAsJsonNull_CoercesToZero()
    {
        // Live failure (2026-08-23): asking "who are you" (a pure identity question with no
        // real numeric metric) made the model (meta/llama-3.1-8b-instruct via NVIDIA) emit
        // "value":null instead of a number. Value is typed double (non-nullable) — strict
        // System.Text.Json deserialization throws on a null token for a non-nullable value
        // type, which failed TryParse for the *entire* array over one field, discarding an
        // otherwise perfectly good "I am an AI HR Analytics Assistant..." answer.
        const string text = """[{"label":"AI HR Analytics Assistant","value":null,"chartable":true}]""";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics.Should().HaveCount(1);
        metrics[0].Value.Should().Be(0.0);
    }

    [Fact]
    public void TryParse_BareObjectWithoutArrayBrackets_TreatsAsSingleRowArray()
    {
        // Live failure (2026-08-23): asking "who are you" made the model emit a single
        // HrMetricRow as a bare JSON object, with no surrounding [ ] at all. The old
        // first-'['-to-last-']' extraction found no brackets whatsoever and returned
        // found=false, discarding the response entirely even though the object itself
        // was perfectly valid.
        const string text = """{"label":"HR Analytics Assistant","value":0,"chartable":false}""";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics.Should().HaveCount(1);
        metrics[0].Label.Should().Be("HR Analytics Assistant");
    }

    [Fact]
    public void ExtractDisplayText_BareObjectWithTrailingSummary_ReturnsOnlySummary()
    {
        const string text = """
            {"label":"HR Analytics Assistant","value":0,"chartable":false}
            I am an AI HR Analytics Assistant.
            """;

        var result = HrMetricParser.ExtractDisplayText(text);

        result.Should().Be("I am an AI HR Analytics Assistant.");
    }

    [Fact]
    public void Parse_RowWithoutColumnNames_DefaultsToNull()
    {
        const string text = """[{"label":"IT","value":8000.0,"category":"AvgSalary"}]""";

        var result = HrMetricParser.Parse(text);

        result[0].LabelName.Should().BeNull();
        result[0].ValueName.Should().BeNull();
        result[0].CategoryName.Should().BeNull();
    }

    [Fact]
    public void Parse_ListingRowWithColumnNames_ParsesRealFieldNames()
    {
        const string text = """
            [{"label":"Steven King","value":24000.0,"category":"Administration","chartable":false,
              "labelName":"Employee Name","valueName":"Salary","categoryName":"Department"}]
            """;

        var result = HrMetricParser.Parse(text);

        result[0].LabelName.Should().Be("Employee Name");
        result[0].ValueName.Should().Be("Salary");
        result[0].CategoryName.Should().Be("Department");
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

    [Fact]
    public void LooksLikeGenuineTextAnswer_PlainProseWithNoJson_ReturnsTrue()
    {
        // Live failure (2026-08-23): asking "what can you do" made the model answer with
        // complete, coherent prose (a bulleted capability list) and no JSON attempt at all —
        // a legitimate conversational answer, not the narration/scaffolding failure the
        // generic fallback message exists to catch.
        const string text = """
            I can access information from the following tables: DEPARTMENTS, EMPLOYEES, JOBS, LOCATIONS.

            I can perform the following actions:

            - Get the list of tables in the database.
            - Get the headcount of a specific department.
            """;

        HrMetricParser.LooksLikeGenuineTextAnswer(text).Should().BeTrue();
    }

    [Fact]
    public void LooksLikeGenuineTextAnswer_DanglingToolCallJson_ReturnsFalse()
    {
        // A genuine bug-060-style failure: the model left broken/dangling JSON scaffolding
        // behind with nothing coherent after it — this must still hit the generic fallback.
        const string text = """{"name": "Employee salary data by department", "arguments": {}}""";

        HrMetricParser.LooksLikeGenuineTextAnswer(text).Should().BeFalse();
    }

    [Fact]
    public void LooksLikeGenuineTextAnswer_EmptyOrWhitespace_ReturnsFalse()
    {
        HrMetricParser.LooksLikeGenuineTextAnswer(string.Empty).Should().BeFalse();
        HrMetricParser.LooksLikeGenuineTextAnswer("   ").Should().BeFalse();
    }

    [Fact]
    public void Parse_RowMissingLabelKey_DefaultsToEmptyStringNotNull()
    {
        // Live failure (2026-08-23, meta/llama-3.1-8b-instruct): the model used "labelName"
        // on every row but never included "label" itself, confusing the header-override
        // field for a replacement of the actual data field. Since Label previously had no
        // default value, System.Text.Json deserialized the missing key as null despite the
        // non-nullable `string` type — and ResultsPanel.BuildChart's r.Label.Length call
        // crashed with a NullReferenceException that took down the entire Blazor circuit
        // (not just this one message). Label must never come back null from TryParse.
        const string text = """[{"labelName":"Employee Name","valueName":"Salary"}]""";

        var found = HrMetricParser.TryParse(text, out var metrics);

        found.Should().BeTrue();
        metrics[0].Label.Should().NotBeNull();
        metrics[0].Label.Should().BeEmpty();
    }
}
