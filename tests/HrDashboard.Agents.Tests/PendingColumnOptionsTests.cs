using FluentAssertions;
using HrDashboard.Agents.Models;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class PendingColumnOptionsTests
{
    [Fact]
    public void GetDefaultSelectedColumns_ExcludesIdLikeColumns()
    {
        var options = new PendingColumnOptions("EMPLOYEES",
            ["EMPLOYEE_ID", "FIRST_NAME", "SALARY", "DEPARTMENT_ID"]);

        var defaults = options.GetDefaultSelectedColumns();

        defaults.Should().BeEquivalentTo("FIRST_NAME", "SALARY");
    }

    [Fact]
    public void GetDefaultSelectedColumns_AllColumnsAreIdLike_ReturnsEmpty()
    {
        var options = new PendingColumnOptions("LINK_TABLE", ["EMPLOYEE_ID", "DEPARTMENT_ID"]);

        var defaults = options.GetDefaultSelectedColumns();

        defaults.Should().BeEmpty();
    }
}
