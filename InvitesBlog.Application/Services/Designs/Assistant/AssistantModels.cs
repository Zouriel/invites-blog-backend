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

/// <param name="Model">The Claude model for this account (see <see cref="AssistantOptions.ModelFor"/>).</param>
/// <param name="Effort">low | medium | high.</param>
public sealed record AssistantModelRequest(
    IReadOnlyList<AssistantTurnMessage> Messages,
    AssistantVocabulary Vocabulary,
    string Model,
    string Effort);

public sealed record AssistantUsageCount(long InputTokens, long CacheWriteTokens, long CacheReadTokens, long OutputTokens);

/// <param name="StopReason">end_turn | tool_use | max_tokens | refusal | pause_turn | ...</param>
public sealed record AssistantModelReply(IReadOnlyList<AssistantBlock> Blocks, string StopReason, AssistantUsageCount Usage);

/// <summary>Claude, behind an interface so tests can stand in for it.</summary>
public interface IAssistantModel
{
    bool Configured { get; }
    Task<AssistantModelReply> CompleteAsync(AssistantModelRequest request, CancellationToken ct = default);
}

/// <summary>US dollars per million tokens for one model. Cache writes are the 5-minute kind (1.25× input).</summary>
public sealed record AssistantPrice(decimal Input, decimal CacheWrite, decimal CacheRead, decimal Output);

/// <summary>
/// Settings: <c>Assistant__ApiKey</c> and friends. Every account gets <see cref="Model"/>; the accounts in
/// <see cref="OwnerUserIds"/> (the owner) get <see cref="OwnerModel"/> and no monthly allowance.
/// </summary>
public sealed class AssistantOptions
{
    public const string Section = "Assistant";

    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low | medium | high.</summary>
    public string Effort { get; set; } = "medium";
    /// <summary>Comma-separated account ids that get <see cref="OwnerModel"/> and no allowance.</summary>
    public string? OwnerUserIds { get; set; }
    public string OwnerModel { get; set; } = "claude-fable-5-1";
    public string OwnerEffort { get; set; } = "medium";
    /// <summary>Per step, thinking included.</summary>
    public int MaxTokens { get; set; } = 16000;
    /// <summary>Most model calls one message from the person may lead to.</summary>
    public int MaxStepsPerMessage { get; set; } = 16;
    /// <summary>How many earlier exchanges the model sees; the chat keeps all of them.</summary>
    public int HistoryTurns { get; set; } = 4;
    /// <summary>The monthly allowance per account, US dollars (owners have none).</summary>
    public decimal MonthlyAllowanceUsd { get; set; } = 5m;

    /// <summary>List prices. A model missing here is charged at the most expensive one, never at nothing.</summary>
    public static readonly IReadOnlyDictionary<string, AssistantPrice> Prices = new Dictionary<string, AssistantPrice>
    {
        ["claude-sonnet-5-5"] = new(2m, 2.5m, 0.2m, 10m),
        ["claude-opus-5-5"] = new(4m, 5m, 0.2m, 20m),
        ["claude-fable-5-1"] = new(10m, 12.5m, 0.25m, 50m),
    };

    public bool IsOwner(Guid userId) =>
        (OwnerUserIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => Guid.TryParse(s, out var id) && id == userId);

    public (string Model, string Effort) ModelFor(Guid userId) => IsOwner(userId) ? (OwnerModel, OwnerEffort) : (Model, Effort);

    public static long CostMicros(string model, AssistantUsageCount u)
    {
        var p = Prices.TryGetValue(model, out var known) ? known : Prices.Values.MaxBy(x => x.Output)!;
        return (long)Math.Ceiling(u.InputTokens * p.Input + u.CacheWriteTokens * p.CacheWrite + u.CacheReadTokens * p.CacheRead + u.OutputTokens * p.Output);
    }
}
