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
}