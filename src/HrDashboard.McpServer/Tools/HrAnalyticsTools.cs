using System.ComponentModel;
using HrDashboard.McpServer;
using ModelContextProtocol.Server;

namespace HrDashboard.McpServer.Tools;

[McpServerToolType]
public sealed class HrAnalyticsTools(IOracleBridge oracle)
{
    [McpServerTool(Name = "GetTopEarnersByDepartment"),
     Description("Returns the top N highest-paid employees grouped by department. Returns JSON array with EmployeeName, DepartmentName, Salary.")]
    public async Task<string> GetTopEarnersByDepartment(
        [Description("Number of top earners to return (default 10)")] int topN = 10,
        CancellationToken ct = default)
    {
        var sql = $"""
            SELECT JSON_ARRAYAGG(
                JSON_OBJECT(
                    'label'    VALUE e.first_name || ' ' || e.last_name,
                    'value'    VALUE e.salary,
                    'category' VALUE NVL(d.department_name, 'No Department')
                )
            ) AS result
            FROM (
                SELECT e.first_name, e.last_name, e.salary, e.department_id
                FROM employees e
                ORDER BY e.salary DESC
                FETCH FIRST {topN} ROWS ONLY
            ) e
            LEFT JOIN departments d ON e.department_id = d.department_id
            """;
        return await oracle.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetSalaryBreakdownByDepartment"),
     Description("Returns average, min, and max salary per department. Returns JSON array with DepartmentName, AvgSalary, MinSalary, MaxSalary, HeadCount.")]
    public async Task<string> GetSalaryBreakdownByDepartment(CancellationToken ct = default)
    {
        const string sql = """
            SELECT JSON_ARRAYAGG(
                JSON_OBJECT(
                    'label'    VALUE NVL(d.department_name, 'No Department'),
                    'value'    VALUE ROUND(AVG(e.salary), 2),
                    'category' VALUE 'AvgSalary'
                )
            ) AS result
            FROM employees e
            LEFT JOIN departments d ON e.department_id = d.department_id
            GROUP BY d.department_name
            ORDER BY AVG(e.salary) DESC
            """;
        return await oracle.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetDeptHeadcount"),
     Description("Returns headcount per department sorted descending.")]
    public async Task<string> GetDeptHeadcount(CancellationToken ct = default)
    {
        const string sql = """
            SELECT JSON_ARRAYAGG(
                JSON_OBJECT(
                    'label' VALUE NVL(d.department_name, 'No Department'),
                    'value' VALUE COUNT(*)
                )
            ) AS result
            FROM employees e
            LEFT JOIN departments d ON e.department_id = d.department_id
            GROUP BY d.department_name
            ORDER BY COUNT(*) DESC
            """;
        return await oracle.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "GetJobSalaryRanges"),
     Description("Returns min and max salary bands per job title from the JOBS table.")]
    public async Task<string> GetJobSalaryRanges(CancellationToken ct = default)
    {
        const string sql = """
            SELECT JSON_ARRAYAGG(
                JSON_OBJECT(
                    'label'    VALUE job_title,
                    'value'    VALUE max_salary,
                    'category' VALUE TO_CHAR(min_salary)
                ) ORDER BY max_salary DESC
            ) AS result
            FROM jobs
            """;
        return await oracle.RunSqlAsync(sql, ct);
    }

    [McpServerTool(Name = "RunHrQuery"),
     Description("Executes an arbitrary read-only SQL query against the HR schema. Use for custom analytics not covered by other tools. Only SELECT statements are permitted.")]
    public async Task<string> RunHrQuery(
        [Description("A valid SELECT SQL statement targeting the HR schema (EMPLOYEES, DEPARTMENTS, JOBS, LOCATIONS)")] string sql,
        CancellationToken ct = default)
    {
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return "[Rejected: only SELECT statements are permitted]";

        return await oracle.RunSqlAsync(sql, ct);
    }
}
