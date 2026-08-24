namespace HrDashboard.Agents.Models;

/// <summary>
/// Real column names captured from a DescribeTable tool result, offered to the user as
/// a clarification for a listing-style request instead of guessing which columns they
/// want. Never constructed from model-authored text — only from an actual tool result,
/// so the offered columns can never be hallucinated.
/// </summary>
public sealed record PendingColumnOptions(string TableName, IReadOnlyList<string> Columns)
{
    /// <summary>
    /// A simple, deterministic default selection: every real column except ones that
    /// look like raw identifier/key columns (name ends in "Id", case-insensitive, which
    /// also covers "_ID" suffixes). Not a perfect heuristic — a column that happens to
    /// end in "id" for another reason would also be excluded — but good enough for a
    /// sensible default the user can freely adjust via the chips.
    /// </summary>
    public HashSet<string> GetDefaultSelectedColumns() =>
        Columns.Where(c => !c.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
               .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
