using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IConversationRepository
{
    Task<List<ConversationSummary>> GetByUserAsync(string userId, CancellationToken ct = default);
    Task<ConversationSummary> CreateAsync(string userId, string title, CancellationToken ct = default);
    Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct = default);

    /// <summary>Returns (Role, Content) only — no MetricsJson. Used for agent context.</summary>
    Task<List<(MessageRole Role, string Content)>> GetMessagesForAgentAsync(
        Guid conversationId, CancellationToken ct = default);

    /// <summary>Returns full message rows including MetricsJson. Used for display.</summary>
    Task<List<MessageDisplay>> GetMessagesForDisplayAsync(
        Guid conversationId, CancellationToken ct = default);

    Task<MessageDisplay> AddMessageAsync(
        Guid conversationId,
        MessageRole role,
        string content,
        string? metricsJson = null,
        CancellationToken ct = default);
}

public record ConversationSummary(Guid Id, string Title, DateTime CreatedAt);

public record MessageDisplay(
    Guid Id,
    MessageRole Role,
    string Content,
    string? MetricsJson,
    DateTime CreatedAt);
