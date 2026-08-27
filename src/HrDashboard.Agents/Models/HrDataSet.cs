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

/// <summary>
/// The small amount of judgment an HrDataSet still needs from the model when its
/// "columns"/"rows" are instead built directly from a tool's raw result (see
/// HrDataSetParser.TryBuildFromRawResult) — a title and an optional chart pairing, never
/// the row data itself.
/// </summary>
public sealed record HrDataSetMeta(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("chartRecommendation")] HrChartRecommendation? ChartRecommendation);

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

    /// <summary>
    /// Parses the lightweight "datasetMeta" object a model emits when the actual row data
    /// comes from <see cref="TryBuildFromRawResult"/> instead of the model's own text — just
    /// a title and an optional chart recommendation, never rows/columns.
    /// </summary>
    public static bool TryParseMeta(string text, out HrDataSetMeta? meta)
    {
        meta = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var marker = text.IndexOf("\"datasetMeta\"", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;

        var objectStart = text.IndexOf('{', marker);
        if (objectStart < 0) return false;
        var objectEnd = FindMatchingBrace(text, objectStart);
        if (objectEnd < 0) return false;

        try
        {
            using var document = JsonDocument.Parse(text[objectStart..(objectEnd + 1)]);
            meta = document.RootElement.Deserialize<HrDataSetMeta>(Options);
            return meta is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds an HrDataSet directly from a data tool's raw JSON result (a "[{...},{...}]"
    /// array of row objects, e.g. RunHrQuery's output) instead of parsing it back out of the
    /// model's own generated text — the model never re-transcribes the data, so token cost
    /// and truncation risk stay flat regardless of row count. Column names come from the
    /// SQL's own SELECT aliases (first-seen order across rows); a column's type is "number"
    /// if any row holds a JSON number for it, "string" otherwise. Returns false for anything
    /// that isn't a non-empty JSON array of objects (including tool error strings like
    /// "[SQL error: ...]", which are intentionally not valid JSON objects-in-array and so
    /// fail here cleanly rather than being misread as data).
    /// </summary>
    public static bool TryBuildFromRawResult(
        string rawJson, HrDataSetMeta? meta, out HrDataSet? dataSet)
    {
        dataSet = null;
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            if (document.RootElement.GetArrayLength() == 0) return false;

            var columnOrder = new List<string>();
            var columnTypes = new Dictionary<string, string>(StringComparer.Ordinal);
            var rows = new List<IReadOnlyDictionary<string, JsonElement>>();

            foreach (var rowElement in document.RootElement.EnumerateArray())
            {
                if (rowElement.ValueKind != JsonValueKind.Object) return false;

                var row = new Dictionary<string, JsonElement>();
                foreach (var property in rowElement.EnumerateObject())
                {
                    row[property.Name] = property.Value.Clone();
                    if (!columnTypes.ContainsKey(property.Name))
                    {
                        columnOrder.Add(property.Name);
                        columnTypes[property.Name] = "string";
                    }
                    if (property.Value.ValueKind == JsonValueKind.Number)
                        columnTypes[property.Name] = "number";
                }
                rows.Add(row);
            }

            if (columnOrder.Count == 0) return false;

            var columns = columnOrder.Select(name => new HrDataColumn(name, columnTypes[name])).ToList();
            var title = string.IsNullOrWhiteSpace(meta?.Title) ? "Query Results" : meta.Title;
            dataSet = new HrDataSet(title, columns, rows, meta?.ChartRecommendation)
                .WithoutInvalidChartRecommendation();
            return true;
        }
    }

    public static string ExtractDisplayText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var marker in DisplayTextMarkers)
        {
            var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var objectStart = text.IndexOf('{', markerIndex);
            if (objectStart < 0) continue;
            var objectEnd = FindMatchingBrace(text, objectStart);
            if (objectEnd < 0) continue;

            // objectStart/objectEnd bound only the VALUE of the "dataset"/"datasetMeta" key
            // (the same scope TryParse/TryParseMeta deserialize) — the wrapping object's own
            // closing brace, e.g. the outer "}" in {"dataset": {...}}, still follows and must
            // be consumed too, or it leaks into the display text as a stray leading "}". Some
            // local models (confirmed live with Ollama/gemma4:12b once tool schemas are present
            // in the same request that produces the final answer) tack on more than one extra
            // "}" beyond the wrapper's own — so consume every stray closing brace here, not just
            // one, until real text (or end of string) is reached.
            var afterValue = objectEnd + 1;
            while (true)
            {
                while (afterValue < text.Length && char.IsWhiteSpace(text[afterValue]))
                    afterValue++;
                if (afterValue >= text.Length || text[afterValue] != '}')
                    break;
                afterValue++;
            }

            return text[afterValue..].Trim();
        }

        return text.Trim();
    }

    // "datasetMeta" first: it's a strict superset match target only in the sense that both
    // markers can appear in the same response family, and checking it first costs nothing
    // since the two never collide as substrings of one another ("datasetMeta" does not
    // contain the literal "dataset" — the character after "dataset" there is 'M', not '"').
    private static readonly string[] DisplayTextMarkers = ["\"datasetMeta\"", "\"dataset\""];

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