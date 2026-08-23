using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HrDashboard.Agents.Models;

/// <summary>
/// A single analytical data point returned by the HR agent.
/// Flexible enough to represent salary averages, headcounts, pay ranges, etc.
/// </summary>
public record HrMetricRow(
    // Both default rather than being required: confirmed live 2026-08-23 that a model can
    // omit "label" entirely (e.g. using "labelName" everywhere instead) — since Label is a
    // reference type, a missing key with no default deserializes to null despite the
    // non-nullable string type, and downstream code (ResultsPanel.BuildChart calling
    // r.Label.Length) crashed the entire Blazor circuit with a NullReferenceException on
    // that null. Value already defaulted to 0.0 implicitly (double can't be null), so this
    // default only changes behavior for Label, but C#'s positional-record rules require
    // every parameter after the first defaulted one to also have a default.
    [property: JsonPropertyName("label"), JsonConverter(typeof(FlexibleStringConverter))]
    string Label = "",
    [property: JsonPropertyName("value"), JsonConverter(typeof(FlexibleDoubleConverter))]
    double Value = 0.0,
    [property: JsonPropertyName("category"), JsonConverter(typeof(FlexibleStringConverter))]
    string? Category = null,
    // Defaults true so every existing curated tool (which only ever emits genuinely
    // chartable data) is unaffected. Only listing-style results — most likely from
    // RunHrQuery — are expected to set this false.
    [property: JsonPropertyName("chartable")]
    bool Chartable = true,
    // Display names for the Label/Value/Category columns in the results table. Null means
    // "use the generic Label/Value/Category headers" (the right default for genuine metrics,
    // where those names are already meaningful). Listing-style results (chartable:false) are
    // expected to set these to the row's real field names (e.g. "Name"/"Salary"/"Department")
    // so the table doesn't show generic axis labels for what's actually a record listing.
    [property: JsonPropertyName("labelName"), JsonConverter(typeof(FlexibleStringConverter))]
    string? LabelName = null,
    [property: JsonPropertyName("valueName"), JsonConverter(typeof(FlexibleStringConverter))]
    string? ValueName = null,
    [property: JsonPropertyName("categoryName"), JsonConverter(typeof(FlexibleStringConverter))]
    string? CategoryName = null);

/// <summary>
/// Reads a JSON string field leniently — LLMs occasionally emit a bare number (e.g. a
/// department ID) or boolean where a string was expected (confirmed live 2026-08-23: the
/// model sent "category":10 for an employee listing). Strict System.Text.Json deserialization
/// throws on any type mismatch here, which previously failed parsing of the *entire* metrics
/// array over one field, not just that value. Coerces non-string scalars to their string form
/// instead of throwing; still throws on structural mismatches (object/array) since those
/// indicate a genuinely malformed payload rather than a type-flexibility quirk.
/// </summary>
internal sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.TryGetInt64(out var i) ? i.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to string.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>
/// Reads a JSON number field leniently — confirmed live 2026-08-23: for a pure identity
/// question ("who are you") with no real numeric metric to report, the model emitted
/// "value":null instead of a number. Value is a non-nullable double, and strict
/// System.Text.Json deserialization throws on a null token there, which — like
/// <see cref="FlexibleStringConverter"/>'s failure mode — failed parsing of the *entire*
/// array over one field. Coerces null (and numeric strings, matching the pre-existing
/// AllowReadingFromString behavior for a plain double) to 0.0 instead of throwing.
/// </summary>
internal sealed class FlexibleDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.Null => 0.0,
            JsonTokenType.String => double.TryParse(reader.GetString(), out var d) ? d : 0.0,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to double.")
        };

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

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

        if (start >= 0 && end > start)
        {
            var json = cleaned[start..(end + 1)];
            try
            {
                metrics = JsonSerializer.Deserialize<List<HrMetricRow>>(json, _opts) ?? [];
                return true;
            }
            catch
            {
                // Fall through to the bare-object fallback below.
            }
        }

        // The model occasionally forgets to wrap a single row in [ ] and emits a bare
        // {"label":...} object instead (confirmed live 2026-08-23, meta/llama-3.1-8b-instruct
        // answering "who are you"). Treat a lone object as a one-element array rather than
        // failing the whole response.
        var objStart = cleaned.IndexOf('{');
        if (objStart < 0) return false;

        var objEnd = FindMatchingBrace(cleaned, objStart);
        if (objEnd < 0) return false;

        try
        {
            var single = JsonSerializer.Deserialize<HrMetricRow>(cleaned[objStart..(objEnd + 1)], _opts);
            if (single is null) return false;

            metrics = [single];
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Distinguishes a genuine plain-text answer from broken/dangling structured output, for
    /// text where <see cref="TryParse"/> already failed to find any HrMetricRow array or
    /// object. Two very different situations both leave <c>TryParse</c> empty-handed: (1) the
    /// model attempted JSON and it came out broken or dangling — e.g. "Calling RunHrQuery
    /// tool..." left hanging with nothing coherent after it — vs. (2) the model correctly
    /// decided a purely conversational question ("who are you", "what can you do") has no HR
    /// data to report and just answered in plain English, never attempting JSON at all. Only
    /// (2) should be shown to the user as-is. The cheap, reliable distinguisher: (1) always
    /// leaves at least one stray '{' or '[' behind (a JSON attempt, however broken);
    /// genuinely brace-free text is (2).
    /// </summary>
    public static bool LooksLikeGenuineTextAnswer(string cleanedText) =>
        !string.IsNullOrWhiteSpace(cleanedText)
        && !cleanedText.Contains('{')
        && !cleanedText.Contains('[');

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
        if (start >= 0)
        {
            var end = FindMatchingBracket(cleaned, start);
            if (end >= 0) return cleaned[(end + 1)..].Trim();
        }

        // Mirrors TryParse's bare-object fallback: a lone {"label":...} object (no [ ]) is
        // still the metrics payload, not part of the natural-language summary — strip it the
        // same way, rather than leaking the raw JSON into the chat bubble.
        var objStart = cleaned.IndexOf('{');
        if (objStart >= 0)
        {
            var objEnd = FindMatchingBrace(cleaned, objStart);
            if (objEnd >= 0) return cleaned[(objEnd + 1)..].Trim();
        }

        return cleaned;
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

    /// <summary>
    /// Finds the index of the '}' that closes the '{' at <paramref name="openIndex"/>,
    /// respecting nested braces and string content. Returns -1 if unbalanced.
    /// </summary>
    private static int FindMatchingBrace(string text, int openIndex)
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

            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
        }

        return -1;
    }
}
