namespace HrDashboard.Infrastructure.Entities;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<Message> Messages { get; set; } = [];
}
