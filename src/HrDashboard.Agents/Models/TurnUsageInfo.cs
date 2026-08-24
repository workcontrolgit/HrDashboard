namespace HrDashboard.Agents.Models;

/// <summary>
/// Accumulated token usage for one user-visible chat turn — summed across every
/// internal LLM call that turn triggered (tool-loop rounds plus the final streamed
/// answer), tagged with the provider/model that produced it. Built from whatever the
/// underlying Microsoft.Extensions.AI provider actually reports; a provider that
/// doesn't report usage for a given call simply contributes nothing to the total.
/// </summary>
public sealed record TurnUsageInfo(
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);
