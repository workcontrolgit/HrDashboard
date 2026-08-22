using System.ComponentModel;
using HrDashboard.McpServer;
using ModelContextProtocol.Server;

namespace HrDashboard.McpServer.Tools;

[McpServerToolType]
public sealed class SqlServerAnalyticsTools(IHrDataBridge sqlServer)
{
    [McpServerTool(Name = "GetTopEarnersByDepartment"),
     Description("Returns the top N highest-paid employees grouped by department. Returns JSON array with EmployeeName, DepartmentName, Salary. This is the complete answer for 'top/highest-paid employees' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetTopEarnersByDepartment(
        [Description("Number of top earners to return (default 10)")] int topN = 10,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT (
                SELECT TOP ({topN})
                    e.first_name + ' ' + e.last_name AS label,
                    e.salary AS value,
                    ISNULL(d.department_name, 'No Department') AS category
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                ORDER BY e.salary DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetSalaryBreakdownByDepartment"),
     Description("Returns the average salary per department, sorted highest to lowest. Returns JSON array with DepartmentName and AvgSalary only — no min/max/headcount. This is the complete answer for 'average salary by department' questions — do not follow up with RunHrQuery or another tool for the same data.")]
    public async Task<string> GetSalaryBreakdownByDepartment(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT ISNULL(d.department_name, 'No Department') AS label,
                       ROUND(AVG(e.salary), 2) AS value,
                       'AvgSalary' AS category
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                GROUP BY d.department_name
                ORDER BY AVG(e.salary) DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetDeptHeadcount"),
     Description("Returns headcount per department sorted descending. This is the complete answer for 'headcount by department' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetDeptHeadcount(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT ISNULL(d.department_name, 'No Department') AS label,
                       COUNT(*) AS value
                FROM employees e
                LEFT JOIN departments d ON e.department_id = d.department_id
                GROUP BY d.department_name
                ORDER BY COUNT(*) DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetJobSalaryRanges"),
     Description("Returns min and max salary bands per job title from the JOBS table. This is the complete answer for 'job salary ranges' questions — do not follow up with another tool for the same data.")]
    public async Task<string> GetJobSalaryRanges(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (
                SELECT job_title AS label,
                       max_salary AS value,
                       CAST(min_salary AS NVARCHAR(50)) AS category
                FROM jobs
                ORDER BY max_salary DESC
                FOR JSON PATH
            ) AS result
            """;
        return await sqlServer.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "RunHrQuery"),
     Description("Last-resort tool for custom analytics that none of the other tools cover. Do NOT use this to re-fetch, verify, or supplement data another tool already returned — if a more specific tool answers the question, use only that one. Only SELECT statements are permitted.")]
    public async Task<string> RunHrQuery(
        [Description("A valid SELECT SQL statement targeting the HR schema (EMPLOYEES, DEPARTMENTS, JOBS, LOCATIONS)")] string sql,
        CancellationToken ct = default)
    {
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "[Rejected: only SELECT statements are permitted]";

        return await sqlServer.RunSqlAsync(sql, ct);
    }
}
