using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Oracle.ManagedDataAccess.Client;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var oracleConnectionString = config.GetConnectionString("Oracle")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Oracle");
var sqlServerConnectionString = config.GetConnectionString("SqlServer")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:SqlServer");

string[] tables = ["EMPLOYEES", "DEPARTMENTS", "JOBS", "LOCATIONS"];

await using var oracleConnection = new OracleConnection(oracleConnectionString);
await oracleConnection.OpenAsync();

await using var sqlConnection = new SqlConnection(sqlServerConnectionString);
await sqlConnection.OpenAsync();

await EnsureSchemaAsync(sqlConnection);

foreach (var table in tables)
{
    Console.WriteLine($"Cloning {table}...");

    await using (var truncateCommand = sqlConnection.CreateCommand())
    {
        truncateCommand.CommandText = $"TRUNCATE TABLE dbo.{table}";
        await truncateCommand.ExecuteNonQueryAsync();
    }

    await using var selectCommand = oracleConnection.CreateCommand();
    selectCommand.CommandText = $"SELECT * FROM {table}";
    await using var reader = await selectCommand.ExecuteReaderAsync();

    using var bulkCopy = new SqlBulkCopy(sqlConnection)
    {
        DestinationTableName = $"dbo.{table}"
    };
    for (var i = 0; i < reader.FieldCount; i++)
        bulkCopy.ColumnMappings.Add(reader.GetName(i), reader.GetName(i));

    await bulkCopy.WriteToServerAsync(reader);
    Console.WriteLine($"  done.");
}

Console.WriteLine("Clone complete.");
return;

static async Task EnsureSchemaAsync(SqlConnection connection)
{
    const string sql = """
        IF OBJECT_ID('dbo.EMPLOYEES') IS NULL
        CREATE TABLE dbo.EMPLOYEES (
            EMPLOYEE_ID     INT             NOT NULL PRIMARY KEY,
            FIRST_NAME      NVARCHAR(20)    NULL,
            LAST_NAME       NVARCHAR(25)    NOT NULL,
            EMAIL           NVARCHAR(25)    NOT NULL,
            PHONE_NUMBER    NVARCHAR(20)    NULL,
            HIRE_DATE       DATETIME2       NOT NULL,
            JOB_ID          NVARCHAR(10)    NOT NULL,
            SALARY          DECIMAL(8,2)    NULL,
            COMMISSION_PCT  DECIMAL(4,2)    NULL,
            MANAGER_ID      INT             NULL,
            DEPARTMENT_ID   INT             NULL
        );

        IF OBJECT_ID('dbo.DEPARTMENTS') IS NULL
        CREATE TABLE dbo.DEPARTMENTS (
            DEPARTMENT_ID   INT             NOT NULL PRIMARY KEY,
            DEPARTMENT_NAME NVARCHAR(30)    NOT NULL,
            MANAGER_ID      INT             NULL,
            LOCATION_ID     INT             NULL
        );

        IF OBJECT_ID('dbo.JOBS') IS NULL
        CREATE TABLE dbo.JOBS (
            JOB_ID          NVARCHAR(10)    NOT NULL PRIMARY KEY,
            JOB_TITLE       NVARCHAR(35)    NOT NULL,
            MIN_SALARY      DECIMAL(6,0)    NULL,
            MAX_SALARY      DECIMAL(6,0)    NULL
        );

        IF OBJECT_ID('dbo.LOCATIONS') IS NULL
        CREATE TABLE dbo.LOCATIONS (
            LOCATION_ID     INT             NOT NULL PRIMARY KEY,
            STREET_ADDRESS  NVARCHAR(40)    NULL,
            CITY            NVARCHAR(30)    NOT NULL,
            STATE_PROVINCE  NVARCHAR(25)    NULL,
            POSTAL_CODE     NVARCHAR(12)    NULL,
            COUNTRY_ID      NCHAR(2)        NULL
        );
        """;

    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
