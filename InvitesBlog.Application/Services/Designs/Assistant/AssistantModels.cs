using System.Text.Json;

namespace InvitesBlog.Application.Services.Designs.Assistant;

/// <summary>
/// One block of an assistant chat entry, in our own shape so it can be stored, sent to the browser
/// and turned back into a Claude request without depending on the SDK's types.
/// </summary>
/// <param name="Type">text | thinking | redacted_thinking | tool_use | tool_result</param>
/// <param name="Id">The tool call's id (tool_use), or the id it answers (tool_result).</param>
/// <param name="Signature">A thinking block's signature, kept exactly.</param>
/// <param name="Data">A redacted thinking block's data.</param>
/// <param name="Summary">A tool result's plain line for the person; never sent to the model.</param>
public sealed record AssistantBlock(
    string Type,
    string? Text = null,
    string? Id = null,
    string? Name = null,
    JsonElement? Input = null,
    string? Signature = null,
    string? Data = null,
    bool? IsError = null,
    string? Summary = null);

/// <summary>A stored chat entry as the model sees it.</summary>
public sealed record AssistantTurnMessage(string Role, IReadOnlyList<AssistantBlock> Blocks);

/// <summary>Names the browser knows and the server doesn't: effect moves and moving stickers.</summary>
public sealed record AssistantVocabulary(IReadOnlyList<AssistantOption> Moves, IReadOnlyList<AssistantOption> Stickers);

public sealed record AssistantOption(string Id, string Label);

public sealed record AssistantModelRequest(
    IReadOnlyList<AssistantTurnMessage> Messages,
    AssistantVocabulary Vocabulary);

public sealed record AssistantUsageCount(long InputTokens, long CacheWriteTokens, long CacheReadTokens, long OutputTokens);

/// <param name="StopReason">end_turn | tool_use | max_tokens | refusal | pause_turn | ...</param>
public sealed record AssistantModelReply(IReadOnlyList<AssistantBlock> Blocks, string StopReason, AssistantUsageCount Usage);

/// <summary>Claude, behind an interface so tests can stand in for it.</summary>
public interface IAssistantModel
{
    bool Configured { get; }
    Task<AssistantModelReply> CompleteAsync(AssistantModelRequest request, CancellationToken ct = default);
}

/// <summary>Settings: <c>Assistant__ApiKey</c> and friends.</summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-sonnet-5-5";
    /// <summary>low | medium | high.</summary>
    public string Effort { get; set; } = "low";
    public int MaxTokens { get; set; } = 4096;
    /// <summary>Most model calls one message from the person may lead to.</summary>
    public int MaxStepsPerMessage { get; set; } = 16;
    /// <summary>How many earlier exchanges the model sees; the chat keeps all of them.</summary>
    public int HistoryTurns { get; set; } = 4;
    /// <summary>The monthly allowance per account, US dollars.</summary>
    public decimal MonthlyAllowanceUsd { get; set; } = 5m;

    // US dollars per million tokens (Claude Sonnet 5.5 list prices).
    public decimal InputPerMTok { get; set; } = 2m;
    public decimal CacheWritePerMTok { get; set; } = 2.5m;
    public decimal CacheReadPerMTok { get; set; } = 0.2m;
    public decimal OutputPerMTok { get; set; } = 10m;

    public long CostMicros(AssistantUsageCount u) => (long)Math.Ceiling(
        u.InputTokens * InputPerMTok + u.CacheWriteTokens * CacheWritePerMTok
        + u.CacheReadTokens * CacheReadPerMTok + u.OutputTokens * OutputPerMTok);
}
