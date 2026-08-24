using FluentAssertions;
using HrDashboard.Agents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace HrDashboard.Agents.Tests;

public class HrAgentServiceTests
{
    // Helper: build a ChatResponse containing plain text (no tool calls)
    private static ChatResponse TextResponse(string text)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        return new ChatResponse([msg]);
    }

    // Helper: build a ChatResponse containing one tool call
    private static ChatResponse ToolCallResponse(string toolName)
    {
        var call = new FunctionCallContent("call-1", toolName, null);
        var msg  = new ChatMessage(ChatRole.Assistant, [call]);
        return new ChatResponse([msg]);
    }

    // Helper: fake IAsyncEnumerable<ChatResponseUpdate> from chunks
    private static async IAsyncEnumerable<ChatResponseUpdate> FakeStream(
        params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.CompletedTask;
        }
    }

    private static HrAgentService Build(IChatClient client)
        => new(client, [], NullLogger<HrAgentService>.Instance);

    // ── AskAsync tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_NoToolCalls_ReturnsDirectResponse()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("The answer is 42.")));

        var sut = Build(client);
        var (raw, metrics, pending) = await sut.AskAsync("test prompt");

        raw.Should().Be("The answer is 42.");
        metrics.Should().BeEmpty(); // no JSON array in "The answer is 42."
        pending.Should().BeNull();
    }

    [Fact]
    public async Task AskAsync_WithOneToolCallThenText_ReturnsParsedMetrics()
    {
        const string finalText = """[{"label":"IT","value":8000,"category":"AvgSalary"}] Done.""";

        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetSalaryBreakdown")),
                  Task.FromResult(TextResponse(finalText)));

        var sut = Build(client);
        var (raw, metrics, pending) = await sut.AskAsync("salary breakdown");

        raw.Should().Be(finalText);
        metrics.Should().HaveCount(1);
        metrics[0].Label.Should().Be("IT");
        metrics[0].Value.Should().Be(8000.0);
        pending.Should().BeNull(); // GetSalaryBreakdown is a data tool, not schema-only
    }

    [Fact]
    public async Task AskAsync_HitsIterationLimit_ReturnsLimitMessage()
    {
        var client = Substitute.For<IChatClient>();
        // Always return a tool call — never terminates naturally
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(ToolCallResponse("loop")));

        var sut = Build(client);
        var (raw, _, pending) = await sut.AskAsync("infinite loop");

        raw.Should().Contain("iteration limit");
        pending.Should().BeNull();
    }

    [Fact]
    public async Task AskAsync_SchemaOnlyThenPlainTextQuestion_ReturnsPendingColumns()
    {
        const string describeTableJson =
            """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"FIRST_NAME","data_type":"VARCHAR2","is_nullable":"YES"}]""";

        var client = Substitute.For<IChatClient>();
        var describeCall = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [describeCall])])),
                  Task.FromResult(TextResponse("Which columns would you like to see?")));

        Func<string, string> describeTableFn = tableName => describeTableJson;
        var tools = new List<AITool> { AIFunctionFactory.Create(describeTableFn, "DescribeTable", null, null) };
        var sut = new HrAgentService(client, tools, NullLogger<HrAgentService>.Instance);

        var (raw, metrics, pending) = await sut.AskAsync("list employees");

        raw.Should().Be("Which columns would you like to see?");
        metrics.Should().BeEmpty();
        pending.Should().NotBeNull();
        pending!.TableName.Should().Be("EMPLOYEES");
        pending.Columns.Should().Equal("EMPLOYEE_ID", "FIRST_NAME");
    }

    // ── AskStreamAsync tests ─────────────────────────────────────────────────

    [Fact]
    public async Task AskStreamAsync_NoToolCalls_YieldsDirectChunks()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult(TextResponse("Hello world")));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "hi"))
            chunks.Add(chunk);

        chunks.Should().ContainSingle().Which.Should().Be("Hello world");
        // GetStreamingResponseAsync must NOT have been called (Phase 2 skipped)
        client.DidNotReceive().GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AskStreamAsync_WithToolCalls_StreamsPhase2()
    {
        var client = Substitute.For<IChatClient>();

        // Phase 1: one tool-call round, then break (no-calls response)
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetData")),
                  Task.FromResult(TextResponse(string.Empty))); // breaks the loop

        // Phase 2 stream
        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("Hello ", "world"));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "query"))
            chunks.Add(chunk);

        chunks.Should().Equal("Hello ", "world");
    }

    [Fact]
    public async Task AskStreamAsync_IterationLimit_YieldsLimitChunk()
    {
        var client = Substitute.For<IChatClient>();
        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(_ => Task.FromResult(ToolCallResponse("loop")));

        var sut    = Build(client);
        var chunks = new List<string>();

        await foreach (var chunk in sut.AskStreamAsync([], "loop forever"))
            chunks.Add(chunk);

        chunks.Should().ContainSingle().Which.Should().Contain("iteration limit");
    }

    [Fact]
    public async Task AskStreamAsync_SchemaOnlyThenPlainTextQuestion_SetsLastPendingColumnOptions()
    {
        const string describeTableJson =
            """[{"column_name":"EMPLOYEE_ID","data_type":"NUMBER","is_nullable":"NO"},{"column_name":"SALARY","data_type":"NUMBER","is_nullable":"YES"}]""";

        var client = Substitute.For<IChatClient>();
        var describeCall = new FunctionCallContent("call-1", "DescribeTable",
            new Dictionary<string, object?> { ["tableName"] = "EMPLOYEES" });

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, [describeCall])])),
                  Task.FromResult(TextResponse(string.Empty))); // breaks Phase 1 loop into Phase 2

        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("Which columns would you like to see?"));

        Func<string, string> describeTableFn = tableName => describeTableJson;
        var tools = new List<AITool> { AIFunctionFactory.Create(describeTableFn, "DescribeTable", null, null) };
        var sut = new HrAgentService(client, tools, NullLogger<HrAgentService>.Instance);

        var chunks = new List<string>();
        await foreach (var chunk in sut.AskStreamAsync([], "list employees"))
            chunks.Add(chunk);

        sut.LastPendingColumnOptions.Should().NotBeNull();
        sut.LastPendingColumnOptions!.TableName.Should().Be("EMPLOYEES");
        sut.LastPendingColumnOptions.Columns.Should().Equal("EMPLOYEE_ID", "SALARY");
    }

    [Fact]
    public async Task AskStreamAsync_WithDataToolCall_LeavesLastPendingColumnOptionsNull()
    {
        var client = Substitute.For<IChatClient>();

        client.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(
                  Task.FromResult(ToolCallResponse("GetDeptHeadcount")),
                  Task.FromResult(TextResponse(string.Empty)));

        client.GetStreamingResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
              .Returns(FakeStream("""[{"label":"IT","value":5,"category":"Headcount"}] Done."""));

        var sut = Build(client);

        await foreach (var _ in sut.AskStreamAsync([], "headcount per department")) { }

        sut.LastPendingColumnOptions.Should().BeNull();
    }
}
