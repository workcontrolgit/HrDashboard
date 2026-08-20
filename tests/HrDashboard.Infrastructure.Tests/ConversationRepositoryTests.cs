using FluentAssertions;
using HrDashboard.Agents.Models;
using HrDashboard.Infrastructure.Entities;
using HrDashboard.Infrastructure.Repositories;
using Xunit;

namespace HrDashboard.Infrastructure.Tests;

public class ConversationRepositoryTests
{
    // ── GetByUserAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetByUserAsync_ReturnsOnlyCallerConversations()
    {
        var factory = new TestDbContextFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Conversations.Add(new Conversation { UserId = "user-a", Title = "A" });
            db.Conversations.Add(new Conversation { UserId = "user-b", Title = "B" });
            await db.SaveChangesAsync();
        }

        var repo   = new ConversationRepository(factory);
        var result = await repo.GetByUserAsync("user-a");

        result.Should().HaveCount(1);
        result[0].Title.Should().Be("A");
    }

    [Fact]
    public async Task GetByUserAsync_OrderedDescendingByCreatedAt()
    {
        var factory = new TestDbContextFactory();
        await using (var db = factory.CreateDbContext())
        {
            db.Conversations.Add(new Conversation
                { UserId = "u", Title = "Old", CreatedAt = DateTime.UtcNow.AddMinutes(-10) });
            db.Conversations.Add(new Conversation
                { UserId = "u", Title = "New", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var repo   = new ConversationRepository(factory);
        var result = await repo.GetByUserAsync("u");

        result[0].Title.Should().Be("New");
        result[1].Title.Should().Be("Old");
    }

    // ── CreateAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_PersistsAndReturnsNonEmptyId()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);

        var summary = await repo.CreateAsync("user-x", "My Conversation");

        summary.Id.Should().NotBeEmpty();
        summary.Title.Should().Be("My Conversation");

        // verify it actually persisted
        var list = await repo.GetByUserAsync("user-x");
        list.Should().HaveCount(1);
    }

    // ── UpdateTitleAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTitleAsync_ChangesTitle()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var summary = await repo.CreateAsync("user-y", "Old Title");

        await repo.UpdateTitleAsync(summary.Id, "New Title");

        var list = await repo.GetByUserAsync("user-y");
        list[0].Title.Should().Be("New Title");
    }

    // ── AddMessageAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddMessageAsync_PersistsMessageWithRole()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-z", "Conv");

        var msg = await repo.AddMessageAsync(
            conv.Id, MessageRole.User, "Hello!", metricsJson: null);

        msg.Id.Should().NotBeEmpty();
        msg.Role.Should().Be(MessageRole.User);
        msg.Content.Should().Be("Hello!");
        msg.MetricsJson.Should().BeNull();
    }

    // ── GetMessagesForAgentAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetMessagesForAgentAsync_ReturnsInChronologicalOrder()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-a", "Chat");

        await repo.AddMessageAsync(conv.Id, MessageRole.User,      "First");
        await repo.AddMessageAsync(conv.Id, MessageRole.Assistant, "Second");

        var messages = await repo.GetMessagesForAgentAsync(conv.Id);

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be(MessageRole.User);
        messages[0].Content.Should().Be("First");
        messages[1].Role.Should().Be(MessageRole.Assistant);
        messages[1].Content.Should().Be("Second");
    }

    // ── GetMessagesForDisplayAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetMessagesForDisplayAsync_ReturnsInChronologicalOrder()
    {
        var factory = new TestDbContextFactory();
        var repo    = new ConversationRepository(factory);
        var conv    = await repo.CreateAsync("user-b", "Display Chat");

        await repo.AddMessageAsync(conv.Id, MessageRole.User,      "Q", metricsJson: null);
        await repo.AddMessageAsync(conv.Id, MessageRole.Assistant, "A", metricsJson: "[{\"label\":\"x\",\"value\":1}]");

        var messages = await repo.GetMessagesForDisplayAsync(conv.Id);

        messages.Should().HaveCount(2);
        messages[0].Content.Should().Be("Q");
        messages[0].MetricsJson.Should().BeNull();
        messages[1].Content.Should().Be("A");
        messages[1].MetricsJson.Should().Contain("label");
    }
}
