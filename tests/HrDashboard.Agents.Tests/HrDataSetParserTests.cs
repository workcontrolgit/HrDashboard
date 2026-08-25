using System.Text.Json;
using FluentAssertions;
using HrDashboard.Agents.Models;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class HrDataSetParserTests
{
    [Fact]
    public void TryParse_HeadcountDataset_PreservesSemanticColumnsAndRecommendation()
    {
        const string response = """
            {"dataset":{"title":"Headcount by Department","columns":[{"name":"Department"},{"name":"Count","type":"number"}],"rows":[{"Department":"Administration","Count":3},{"Department":"IT","Count":2}],"chartRecommendation":{"xAxisColumn":"Department","yAxisColumn":"Count","reason":"One department and one numeric count."}}
            }
            The headcount for each department is listed above.
            """;

        var found = HrDataSetParser.TryParse(response, out var dataSet);

        found.Should().BeTrue();
        dataSet.Should().NotBeNull();
        dataSet!.Columns.Select(column => column.Name).Should().Equal("Department", "Count");
        dataSet.Rows.Should().HaveCount(2);
        dataSet.Rows[0]["Department"].GetString().Should().Be("Administration");
        dataSet.Rows[0]["Count"].GetInt32().Should().Be(3);
        dataSet.ChartRecommendation!.XAxisColumn.Should().Be("Department");
        dataSet.ChartRecommendation.YAxisColumn.Should().Be("Count");
    }

    [Fact]
    public void TryParse_WideListing_PreservesAllColumns()
    {
        const string response = """
            {"dataset":{"title":"Employees","columns":[{"name":"Employee Name"},{"name":"Department"},{"name":"Job Title"},{"name":"Salary","type":"number"}],"rows":[{"Employee Name":"Steven King","Department":"Administration","Job Title":"President","Salary":24000}]}}
            """;

        HrDataSetParser.TryParse(response, out var dataSet).Should().BeTrue();

        dataSet!.Columns.Should().HaveCount(4);
        dataSet.Rows[0].Keys.Should().Contain(new[] { "Employee Name", "Department", "Job Title", "Salary" });
        dataSet.ChartRecommendation.Should().BeNull();
    }

    [Fact]
    public void TryParse_InvalidRecommendation_DropsRecommendationButKeepsData()
    {
        const string response = """
            {"dataset":{"title":"Departments","columns":[{"name":"Department"},{"name":"Count","type":"number"}],"rows":[],"chartRecommendation":{"xAxisColumn":"Missing","yAxisColumn":"Count"}}}
            """;

        HrDataSetParser.TryParse(response, out var dataSet).Should().BeTrue();

        dataSet!.Rows.Should().BeEmpty();
        dataSet.ChartRecommendation.Should().BeNull();
    }

    [Fact]
    public void TryParseMeta_ValidMeta_ParsesTitleAndChartRecommendation()
    {
        const string response = """
            {"datasetMeta":{"title":"Employees in IT","chartRecommendation":{"xAxisColumn":"Employee Name","yAxisColumn":"Salary","reason":"Compare pay across employees."}}}
            Found 5 employees in IT.
            """;

        var found = HrDataSetParser.TryParseMeta(response, out var meta);

        found.Should().BeTrue();
        meta.Should().NotBeNull();
        meta!.Title.Should().Be("Employees in IT");
        meta.ChartRecommendation!.XAxisColumn.Should().Be("Employee Name");
        meta.ChartRecommendation.YAxisColumn.Should().Be("Salary");
    }

    [Fact]
    public void TryParseMeta_NullChartRecommendation_ParsesTitleOnly()
    {
        const string response = """{"datasetMeta":{"title":"Departments","chartRecommendation":null}}""";

        HrDataSetParser.TryParseMeta(response, out var meta).Should().BeTrue();

        meta!.Title.Should().Be("Departments");
        meta.ChartRecommendation.Should().BeNull();
    }

    [Fact]
    public void TryParseMeta_NoMarker_ReturnsFalse()
    {
        const string response = "No employees match that department.";

        HrDataSetParser.TryParseMeta(response, out var meta).Should().BeFalse();
        meta.Should().BeNull();
    }

    [Fact]
    public void TryBuildFromRawResult_ValidRows_InfersColumnsTypesAndOrder()
    {
        const string rawJson = """
            [{"Department Name":"Administration","Manager Name":"Steven King","Salary":24000.0},{"Department Name":"IT","Manager Name":"Lex De Haan","Salary":9000.0}]
            """;
        var meta = new HrDataSetMeta("Departments and Managers", null);

        var built = HrDataSetParser.TryBuildFromRawResult(rawJson, meta, out var dataSet);

        built.Should().BeTrue();
        dataSet!.Title.Should().Be("Departments and Managers");
        dataSet.Columns.Select(c => c.Name).Should().Equal("Department Name", "Manager Name", "Salary");
        dataSet.Columns.First(c => c.Name == "Salary").Type.Should().Be("number");
        dataSet.Columns.First(c => c.Name == "Department Name").Type.Should().Be("string");
        dataSet.Rows.Should().HaveCount(2);
        dataSet.Rows[0]["Department Name"].GetString().Should().Be("Administration");
        dataSet.Rows[1]["Salary"].GetDouble().Should().Be(9000.0);
    }

    [Fact]
    public void TryBuildFromRawResult_NullMeta_UsesDefaultTitle()
    {
        const string rawJson = """[{"City":"Seattle"}]""";

        HrDataSetParser.TryBuildFromRawResult(rawJson, meta: null, out var dataSet).Should().BeTrue();

        dataSet!.Title.Should().Be("Query Results");
    }

    [Fact]
    public void TryBuildFromRawResult_EmptyArray_ReturnsFalse()
    {
        HrDataSetParser.TryBuildFromRawResult("[]", meta: null, out var dataSet).Should().BeFalse();
        dataSet.Should().BeNull();
    }

    [Theory]
    [InlineData("[Rejected: only SELECT statements are permitted]")]
    [InlineData("[SQL error: Invalid column name 'X']")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryBuildFromRawResult_NotAValidRowArray_ReturnsFalse(string rawResult)
    {
        HrDataSetParser.TryBuildFromRawResult(rawResult, meta: null, out var dataSet).Should().BeFalse();
        dataSet.Should().BeNull();
    }

    [Fact]
    public void TryBuildFromRawResult_ChartRecommendationReferencesRealColumn_IsKept()
    {
        const string rawJson = """[{"Employee Name":"Steven King","Salary":24000.0}]""";
        var meta = new HrDataSetMeta("Employees",
            new HrChartRecommendation("Employee Name", "Salary", "Compare pay."));

        HrDataSetParser.TryBuildFromRawResult(rawJson, meta, out var dataSet).Should().BeTrue();

        dataSet!.ChartRecommendation.Should().NotBeNull();
        dataSet.ChartRecommendation!.YAxisColumn.Should().Be("Salary");
    }

    [Fact]
    public void TryBuildFromRawResult_ChartRecommendationReferencesMissingColumn_IsDropped()
    {
        const string rawJson = """[{"Employee Name":"Steven King"}]""";
        var meta = new HrDataSetMeta("Employees",
            new HrChartRecommendation("Employee Name", "Salary", "Salary isn't in the result."));

        HrDataSetParser.TryBuildFromRawResult(rawJson, meta, out var dataSet).Should().BeTrue();

        dataSet!.ChartRecommendation.Should().BeNull();
    }

    [Fact]
    public void ExtractDisplayText_DatasetMetaMarker_StripsMetaObjectOnly()
    {
        const string response = """
            {"datasetMeta":{"title":"Employees","chartRecommendation":null}}
            Found 5 employees.
            """;

        var displayText = HrDataSetParser.ExtractDisplayText(response);

        displayText.Should().Be("Found 5 employees.");
    }

    [Fact]
    public void ExtractDisplayText_LegacyDatasetMarker_StillStripsFullObject()
    {
        const string response = """
            {"dataset":{"title":"Departments","columns":[{"name":"Department"}],"rows":[{"Department":"IT"}]}}
            Here are the departments.
            """;

        var displayText = HrDataSetParser.ExtractDisplayText(response);

        displayText.Should().Be("Here are the departments.");
    }
}