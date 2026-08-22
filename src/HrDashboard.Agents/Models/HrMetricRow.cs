using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    // Local LLMs (e.g. Ollama) occasionally echo the raw tool-call announcement and/or
    // OllamaSharp's serialized FunctionResultContent (itself containing a nested JSON array)
    // ahead of the real HrMetricRow payload. That nested array's '[' would otherwise be picked
    // up by naive first-'['-to-last-']' slicing and produce malformed, unparseable JSON.
    private static readonly Regex PayloadStart = new("\\[\\s*\\{\\s*\"label\"", RegexOptions.Compiled);

    /// <summary>
    /// Strips any tool-call/tool-result scaffolding a local LLM may echo before the intended
    /// HrMetricRow JSON array. Returns the text unchanged if no such array is found.
    /// </summary>
    public static string StripScaffolding(string llmText)
    {
        if (string.IsNullOrEmpty(llmText)) return llmText;
        var match = PayloadStart.Match(llmText);
        return match.Success ? llmText[match.Index..] : llmText;
    }

    /// <summary>
    /// Extracts HrMetricRow[] from LLM text. Looks for a JSON array anywhere in the response.
    /// Falls back to empty array — never throws. Does not distinguish a genuine empty result
    /// (model correctly answered "no rows match") from no array being present at all; use
    /// <see cref="TryParse"/> when that distinction matters.
    /// </summary>
    public static IReadOnlyList<HrMetricRow> Parse(string llmText)
    {
        TryParse(llmText, out var metrics);
        return metrics;
    }

    /// <summary>
    /// Extracts HrMetricRow[] from LLM text, same as <see cref="Parse"/>, but the return value
    /// tells the caller whether a JSON array was actually present — a genuine empty result
    /// (e.g. "no departments have more than 5 employees" correctly emitting "[]") must be
    /// distinguished from narration/scaffolding with no array at all, since both otherwise
    /// produce an empty list.
    /// </summary>
    public static bool TryParse(string llmText, out IReadOnlyList<HrMetricRow> metrics)
    {
        metrics = [];
        if (string.IsNullOrWhiteSpace(llmText)) return false;

        var cleaned = StripScaffolding(llmText);

        // Find first '[' and last ']' to extract JSON array
        var start = cleaned.IndexOf('[');
        var end   = cleaned.LastIndexOf(']');

        if (start < 0 || end <= start) return false;

        var json = cleaned[start..(end + 1)];

        try
        {
            metrics = JsonSerializer.Deserialize<List<HrMetricRow>>(json, _opts) ?? [];
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns just the natural-language portion of the LLM's response, with the leading
    /// HrMetricRow JSON array (and any scaffolding before it) removed. The array already
    /// drives the chart via <see cref="Parse"/>; showing it again in the chat transcript is
    /// redundant and confusing. Returns the cleaned text unchanged if no array is found.
    /// </summary>
    public static string ExtractDisplayText(string llmText)
    {
        if (string.IsNullOrEmpty(llmText)) return llmText;

        var cleaned = StripScaffolding(llmText);
        var start = cleaned.IndexOf('[');
        if (start < 0) return cleaned;

        var end = FindMatchingBracket(cleaned, start);
        if (end < 0) return cleaned;

        return cleaned[(end + 1)..].Trim();
    }

    /// <summary>
    /// Finds the index of the ']' that closes the '[' at <paramref name="openIndex"/>,
    /// respecting nested brackets and string content. Returns -1 if unbalanced.
    /// </summary>
    private static int FindMatchingBracket(string text, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escapeNext) { escapeNext = false; continue; }
            if (c == '\\' && inString) { escapeNext = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }

        return -1;
    }
}
