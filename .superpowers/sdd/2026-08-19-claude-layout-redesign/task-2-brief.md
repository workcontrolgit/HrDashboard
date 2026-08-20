# Task 2 Brief: AskStreamAsync on IHrAgentService + HrAgentService

## Context
Task 2 of 13. Adds streaming support to the agent layer.
Repository: c:/apps/HrDashboard, branch: feature/claude-layout-redesign.
Task 1 is complete — MessageRole enum and IConversationRepository interface exist.

## Global Constraints
- net10.0, nullable enabled, implicit usings enabled
- `HrMetricParser` already exists in `src/HrDashboard.Agents/Models/HrMetricRow.cs` — do NOT recreate or move it
- Uses `Microsoft.Extensions.AI` v10.9.0 — `IChatClient` has `GetResponseAsync` (non-streaming) and `GetStreamingResponseAsync` (streaming, returns `IAsyncEnumerable<ChatResponseUpdate>` where `.Text` is the string delta)
- No subagents — implement, build, commit, write report yourself

## Files to Modify

### 1. Replace `src/HrDashboard.Agents/IHrAgentService.cs` with:
```csharp
using HrDashboard.Agents.Models;

namespace HrDashboard.Agents;

public interface IHrAgentService
{
    /// <summary>
    /// Submits a natural language HR analytics query and returns structured metric rows.
    /// </summary>
    Task<(string RawText, IReadOnlyList<HrMetricRow> Metrics)> AskAsync(
        string prompt,
        CancellationToken ct = default);

    /// <summary>
    /// Streams the AI response chunk by chunk. Runs the tool-use loop internally
    /// (non-streaming), then yields the final text as it arrives.
    /// history = prior (Role, Content) pairs for context — MetricsJson is never included.
    /// </summary>
    IAsyncEnumerable<string> AskStreamAsync(
        IEnumerable<(MessageRole Role, string Content)> history,
        string prompt,
        CancellationToken ct = default);
}
```

### 2. Add to `src/HrDashboard.Agents/HrAgentService.cs`

Add the `BuildMessages` private helper method and implement `AskStreamAsync`. Insert both BEFORE the existing `InvokeToolAsync` method. Do NOT change any existing code.

**BuildMessages helper:**
```csharp
private List<ChatMessage> BuildMessages(
    IEnumerable<(MessageRole Role, string Content)> history,
    string prompt)
{
    var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt) };
    foreach (var (role, content) in history)
    {
        var chatRole = role == MessageRole.User ? ChatRole.User : ChatRole.Assistant;
        messages.Add(new ChatMessage(chatRole, content));
    }
    messages.Add(new ChatMessage(ChatRole.User, prompt));
    return messages;
}
```

**AskStreamAsync implementation:**
```csharp
public async IAsyncEnumerable<string> AskStreamAsync(
    IEnumerable<(MessageRole Role, string Content)> history,
    string prompt,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
{
    await EnsureInitializedAsync(ct);

    _logger.LogInformation("HR agent streaming prompt: {Prompt}", prompt);

    var toolOptions = new ChatOptions { Tools = [.. _tools] };
    var messages = BuildMessages(history, prompt);

    // Phase 1: tool-use loop (non-streaming) to gather Oracle HR data
    for (int i = 0; i < MaxIterations; i++)
    {
        var response = await _chatClient.GetResponseAsync(messages, toolOptions, ct);
        foreach (var msg in response.Messages)
            messages.Add(msg);

        var calls = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        if (calls.Count == 0)
        {
            // Model answered without tool calls — yield full text as single chunk
            _logger.LogInformation("Agent streaming completed in {Iterations} iteration(s)", i + 1);
            yield return response.Text ?? string.Empty;
            yield break;
        }

        if (i == MaxIterations - 1)
        {
            _logger.LogWarning("Agent streaming hit iteration limit for prompt: {Prompt}", prompt);
            yield return "[Agent reached iteration limit — rephrase your query]";
            yield break;
        }

        foreach (var call in calls)
        {
            _logger.LogDebug("Tool call: {Tool}", call.Name);
            var result = await InvokeToolAsync(call, ct);
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent(call.CallId, result)]));
        }
    }

    // Phase 2: tool data gathered — stream the final summarization
    await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, new ChatOptions(), ct))
    {
        if (!string.IsNullOrEmpty(update.Text))
            yield return update.Text;
    }
}
```

## Steps
1. Replace IHrAgentService.cs with the code above
2. Add BuildMessages and AskStreamAsync to HrAgentService.cs (before InvokeToolAsync, do NOT change existing code)
3. Run: `dotnet build src/HrDashboard.Agents/HrDashboard.Agents.csproj`
4. Verify: Build succeeded, 0 errors
5. Commit:
   ```
   git add src/HrDashboard.Agents/IHrAgentService.cs src/HrDashboard.Agents/HrAgentService.cs
   git commit -m "feat(agents): add AskStreamAsync with tool-use loop and streaming final response"
   ```
6. Write report to: `.superpowers/sdd/2026-08-19-claude-layout-redesign/task-2-report.md`

## Report Contract
Status: DONE | DONE_WITH_CONCERNS | NEEDS_CONTEXT | BLOCKED
Commits: <sha>
Test summary: build result (pass/fail, 0 errors)
Concerns: (if any)
