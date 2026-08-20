using HrDashboard.Agents.Models;

namespace HrDashboard.Web.Services;

public class MessageViewModel
{
    public MessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public bool IsStreaming { get; set; }
    public IReadOnlyList<HrMetricRow>? Metrics { get; set; }

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
