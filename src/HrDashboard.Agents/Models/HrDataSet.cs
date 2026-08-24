using System.Text.Json;
using System.Text.Json.Serialization;

namespace HrDashboard.Agents.Models;

public sealed record HrDataColumn(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type = "string");

public sealed record HrChartRecommendation(
    [property: JsonPropertyName("xAxisColumn")] string XAxisColumn,
    [property: JsonPropertyName("yAxisColumn")] string YAxisColumn,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record HrDataSet(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("columns")] IReadOnlyList<HrDataColumn> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows,
    [property: JsonPropertyName("chartRecommendation")] HrChartRecommendation? ChartRecommendation = null)
{
    public bool HasColumn(string name) =>
        Columns.Any(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));

    public HrDataSet WithoutInvalidChartRecommendation()
    {
        if (ChartRecommendation is null
            || !HasColumn(ChartRecommendation.XAxisColumn)
            || !HasColumn(ChartRecommendation.YAxisColumn))
            return this with { ChartRecommendation = null };

        var yColumn = Columns.First(column =>
            string.Equals(column.Name, ChartRecommendation.YAxisColumn, StringComparison.OrdinalIgnoreCase));
        return string.Equals(yColumn.Type, "number", StringComparison.OrdinalIgnoreCase)
            ? this
            : this with { ChartRecommendation = null };
    }

    public static HrDataSet FromLegacyMetrics(IReadOnlyList<HrMetricRow> metrics) =>
        new(
            "HR data",
            [
                new HrDataColumn("Label"),
                new HrDataColumn("Value", "number"),
                new HrDataColumn("Category")
            ],
            metrics.Select(metric => (IReadOnlyDictionary<string, JsonElement>)new Dictionary<string, JsonElement>
            {
                ["Label"] = JsonSerializer.SerializeToElement(metric.Label),
                ["Value"] = JsonSerializer.SerializeToElement(metric.Value),
                ["Category"] = JsonSerializer.SerializeToElement(metric.Category)
            }).ToList());
}

public static class HrDataSetParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string text, out HrDataSet? dataSet)
    {
        dataSet = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var datasetMarker = text.IndexOf("\"dataset\"", StringComparison.OrdinalIgnoreCase);
        if (datasetMarker < 0) return false;

        var objectStart = text.IndexOf('{', datasetMarker);
        if (objectStart < 0) return false;
        var objectEnd = FindMatchingBrace(text, objectStart);
        if (objectEnd < 0) return false;

        try
        {
            using var document = JsonDocument.Parse(text[objectStart..(objectEnd + 1)]);
            var parsed = document.RootElement.Deserialize<HrDataSet>(Options);
            if (parsed is null || parsed.Columns.Count == 0) return false;

            dataSet = parsed.WithoutInvalidChartRecommendation();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ExtractDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var marker = text.IndexOf("\"dataset\"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return text.Trim();
        var objectStart = text.IndexOf('{', marker);
        if (objectStart < 0) return text.Trim();
        var objectEnd = FindMatchingBrace(text, objectStart);
        return objectEnd < 0 ? text.Trim() : text[(objectEnd + 1)..].Trim();
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = openIndex; index < text.Length; index++)
        {
            var character = text[index];
            if (escaped) { escaped = false; continue; }
            if (character == '\\' && inString) { escaped = true; continue; }
            if (character == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return index;
        }
        return -1;
    }
}