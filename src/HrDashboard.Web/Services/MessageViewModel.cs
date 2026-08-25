using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class MessageViewModel
{
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public IReadOnlyList<HrMetricRow>? Metrics { get; set; }
    public HrDataSet? DataSet { get; set; }
    public bool ChartConfirmed { get; set; }

    /// <summary>Real columns offered for a listing-style clarification. Session-only —
    /// never persisted (see spec Design Decision 4).</summary>
    public PendingColumnOptions? PendingColumns { get; set; }

    /// <summary>The user's current chip toggle state for <see cref="PendingColumns"/>.
    /// Initialized to a sensible default when PendingColumns is set.</summary>
    public HashSet<string>? SelectedColumns { get; set; }

    /// <summary>True once the user has tapped "Show results" — the chip row becomes
    /// display-only after this so scrolling back doesn't offer stale re-submission.</summary>
    public bool ColumnsConfirmed { get; set; }

    public static MessageViewModel FromUser(string content) => new()
    {
        Role = MessageRole.User,
        Content = content
    };

    public static MessageViewModel StreamingAssistant() => new()
    {
        Role = MessageRole.Assistant,
        IsStreaming = true
    };
}
