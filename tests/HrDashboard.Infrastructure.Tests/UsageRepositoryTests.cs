using FluentAssertions;
using HrDashboard.Agents;
using HrDashboard.Agents.Models;
using HrDashboard.Infrastructure.Entities;
using HrDashboard.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HrDashboard.Infrastructure.Tests;

public class UsageRepositoryTests
{
    private static async Task<Guid> CreateMessageAsync(TestDbContextFactory factory, string userId)
    {
        var convRepo = new ConversationRepository(factory);
        var conv = await convRepo.CreateAsync(userId, "Chat");
        var msg  = await convRepo.AddMessageAsync(conv.Id, MessageRole.Assistant, "Answer");
        return msg.Id;
    }

    [Fact]
    public async Task AddUsageRecordAsync_PersistsRecord()
    {
        var factory    = new TestDbContextFactory();
        var messageId  = await CreateMessageAsync(factory, "user-a");
        var repo       = new UsageRepository(factory);

        await repo.AddUsageRecordAsync(
            messageId, "user-a", "Nvidia", "meta/llama-3.1-8b-instruct", 100, 20, 120);

        var breakdown = await repo.GetProviderModelBreakdownAsync("user-a");
        breakdown.Should().ContainSingle();
        breakdown[0].Provider.Should().Be("Nvidia");
        breakdown[0].InputTokens.Should().Be(100);
        breakdown[0].OutputTokens.Should().Be(20);
        breakdown[0].TotalTokens.Should().Be(120);
        breakdown[0].TurnCount.Should().Be(1);
    }

    [Fact]
    public async Task GetProviderModelBreakdownAsync_OnlyReturnsCallerRecords()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var messageA = await CreateMessageAsync(factory, "user-a");
        var messageB = await CreateMessageAsync(factory, "user-b");

        await repo.AddUsageRecordAsync(messageA, "user-a", "Ollama", "qwen3-vl:8b", 10, 5, 15);
        await repo.AddUsageRecordAsync(messageB, "user-b", "Ollama", "qwen3-vl:8b", 999, 999, 1998);

        var breakdown = await repo.GetProviderModelBreakdownAsync("user-a");

        breakdown.Should().ContainSingle();
        breakdown[0].TotalTokens.Should().Be(15);
    }

    [Fact]
    public async Task GetProviderModelBreakdownAsync_SumsAcrossMultipleTurnsForSameProviderModel()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var userId   = "user-c";
        var message1 = await CreateMessageAsync(factory, userId);
        var message2 = await CreateMessageAsync(factory, userId);

        await repo.AddUsageRecordAsync(message1, userId, "Nvidia", "meta/llama-3.1-8b-instruct", 100, 20, 120);
        await repo.AddUsageRecordAsync(message2, userId, "Nvidia", "meta/llama-3.1-8b-instruct", 50, 10, 60);

        var breakdown = await repo.GetProviderModelBreakdownAsync(userId);

        breakdown.Should().ContainSingle();
        breakdown[0].InputTokens.Should().Be(150);
        breakdown[0].OutputTokens.Should().Be(30);
        breakdown[0].TotalTokens.Should().Be(180);
        breakdown[0].TurnCount.Should().Be(2);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_SumsMultipleRecordsOnTheSameDay()
    {
        var factory  = new TestDbContextFactory();
        var repo     = new UsageRepository(factory);
        var userId   = "user-e";
        var message1 = await CreateMessageAsync(factory, userId);
        var message2 = await CreateMessageAsync(factory, userId);

        await repo.AddUsageRecordAsync(message1, userId, "Ollama", "qwen3-vl:8b", 10, 10, 20);
        await repo.AddUsageRecordAsync(message2, userId, "Ollama", "qwen3-vl:8b", 5, 5, 10);

        var totals = await repo.GetDailyTotalsAsync(userId, days: 30);

        totals.Should().ContainSingle();
        totals[0].TotalTokens.Should().Be(30);
    }

    [Fact]
    public async Task GetDailyTotalsAsync_ExcludesRecordsOutsideTheRequestedWindow()
    {
        var factory              = new TestDbContextFactory();
        var repo                 = new UsageRepository(factory);
        var userId               = "user-d";
        var messageInWindow      = await CreateMessageAsync(factory, userId);
        var messageOutsideWindow = await CreateMessageAsync(factory, userId);

        await using (var db = factory.CreateDbContext())
        {
            db.UsageRecords.Add(new UsageRecord
            {
                MessageId = messageInWindow, UserId = userId, Provider = "Ollama", Model = "qwen3-vl:8b",
                InputTokens = 10, OutputTokens = 10, TotalTokens = 20, CreatedAt = DateTime.UtcNow
            });
            db.UsageRecords.Add(new UsageRecord
            {
                MessageId = messageOutsideWindow, UserId = userId, Provider = "Ollama", Model = "qwen3-vl:8b",
                InputTokens = 999, OutputTokens = 999, TotalTokens = 1998, CreatedAt = DateTime.UtcNow.AddDays(-60)
            });
            await db.SaveChangesAsync();
        }

        var totals = await repo.GetDailyTotalsAsync(userId, days: 30);

        totals.Should().ContainSingle();
        totals[0].TotalTokens.Should().Be(20);
    }
}
