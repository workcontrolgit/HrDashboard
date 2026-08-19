using System.Text.Json;
using System.Text.Json.Serialization;

namespace HrDashboard.Agents.Models;

/// <summary>
/// A single analytical data point returned by the HR agent.
/// Flexible enough to represent salary averages, headcounts, pay ranges, etc.
/// </summary>
public record HrMetricRow(
    [property: JsonPropertyName("label")]    string Label,
    [property: JsonPropertyName("value")]    double Value,
    [property: JsonPropertyName("category")] string? Category = null);

public static class HrMetricParser
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Extracts HrMetricRow[] from LLM text. Looks for a JSON array anywhere in the response.
    /// Falls back to empty array — never throws.
    /// </summary>
    public static IReadOnlyList<HrMetricRow> Parse(string llmText)
    {
        if (string.IsNullOrWhiteSpace(llmText)) return [];

        // Find first '[' and last ']' to extract JSON array
        var start = llmText.IndexOf('[');
        var end   = llmText.LastIndexOf(']');

        if (start < 0 || end <= start) return [];

        var json = llmText[start..(end + 1)];

        try
        {
            return JsonSerializer.Deserialize<List<HrMetricRow>>(json, _opts) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
