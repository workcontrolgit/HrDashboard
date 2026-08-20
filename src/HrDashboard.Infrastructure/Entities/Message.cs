using HrDashboard.Agents.Models;

namespace HrDashboard.Infrastructure.Entities;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;
    public MessageRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>Serialized List&lt;HrMetricRow&gt;. Display only — never sent to agent.</summary>
    public string? MetricsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
