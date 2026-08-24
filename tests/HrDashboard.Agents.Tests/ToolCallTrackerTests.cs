using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class ToolCallTrackerTests
{
    private const string ValidDescribeTableJson =
        """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"FIRST_NAME","data_type":"VARCHAR2","is_nullable":"YES"},{"column_name":"SALARY","data_type":"NUMBER","is_nullable":"YES"}]""";

    [Fact]
    public void Observe_DescribeTableWithValidJson_CapturesColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        tracker.Observe(call, ValidDescribeTableJson);

        tracker.LastDescribeTableName.Should().Be("EMPLOYEES");
        tracker.LastDescribeTableColumns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME", "SALARY");
        tracker.DataToolCalled.Should().BeFalse();
    }

    [Fact]
    public void Observe_DataToolCall_SetsDataToolCalled()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "RunHrQuery", null);

        tracker.Observe(call, "[]");

        tracker.DataToolCalled.Should().BeTrue();
    }

    [Fact]
    public void Observe_ListTablesCall_DoesNotSetDataToolCalledOrColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "ListTables", null);

        tracker.Observe(call, """["EMPLOYEES","DEPARTMENTS"]""");

        tracker.DataToolCalled.Should().BeFalse();
        tracker.LastDescribeTableColumns.Should().BeNull();
    }

    [Fact]
    public void Observe_DescribeTableWithErrorString_DoesNotCaptureColumns()
    {
        var tracker = new ToolCallTracker();
        var call = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "NOPE" });

        tracker.Observe(call, "[Rejected: table not in the allowed HR table list]");

        tracker.LastDescribeTableColumns.Should().BeNull();
    }

    [Fact]
    public void Classify_NoDescribeTableCalled_ReturnsNull()
    {
        var tracker = new ToolCallTracker();

        var result = tracker.Classify("Here's a plain answer.");

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_DataToolCalledEvenAfterDescribeTable_ReturnsNull()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);
        tracker.Observe(new FunctionCallContent("call-2", "RunHrQuery", null), "[]");

        var result = tracker.Classify("Here are the results.");

        result.Should().BeNull();
    }

    [Fact]
    public void Classify_SchemaOnlyAndPlainTextAnswer_ReturnsPendingColumnOptions()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);

        var result = tracker.Classify("Which columns would you like to see?");

        result.Should().NotBeNull();
        result!.TableName.Should().Be("EMPLOYEES");
        result.Columns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME", "SALARY");
    }

    [Fact]
    public void Classify_TwoTablesDescribed_QualifiesColumnsByTable()
    {
        const string departmentsJson =
            """[{"column_name":"DEPARTMENT_ID"},{"column_name":"DEPARTMENT_NAME"},{"column_name":"MANAGER_ID"}]""";
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "DEPARTMENTS" }), departmentsJson);
        tracker.Observe(new FunctionCallContent("call-2", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);

        var result = tracker.Classify("Which columns would you like to see?");

        result.Should().NotBeNull();
        result!.TableName.Should().Be("DEPARTMENTS, EMPLOYEES");
        result.Columns.Should().Equal(
            "DEPARTMENTS.DEPARTMENT_ID", "DEPARTMENTS.DEPARTMENT_NAME", "DEPARTMENTS.MANAGER_ID",
            "EMPLOYEES.EMPLOYEE_ID", "EMPLOYEES.FIRST_NAME", "EMPLOYEES.SALARY");
    }

    [Fact]
    public void Classify_SchemaOnlyButTextContainsJsonArray_ReturnsNull()
    {
        var tracker = new ToolCallTracker();
        tracker.Observe(new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" }), ValidDescribeTableJson);

        var result = tracker.Classify("""[{"label":"IT","value":1,"category":"x"}] Done.""");

        result.Should().BeNull();
    }
}
