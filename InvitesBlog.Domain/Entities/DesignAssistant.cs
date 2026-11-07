namespace InvitesBlog.Domain.Entities;

/// <summary>
/// One entry of a design's assistant chat, kept so the person can come back to the conversation.
/// <see cref="Role"/> is "user" (what the person typed), "assistant" (Claude's reply: text and tool
/// calls) or "results" (what the browser reported back after running those tool calls).
///
/// <para><see cref="ContentJson"/> holds the blocks in the shape the assistant service writes them;
/// thinking blocks keep their signatures exactly, because Claude refuses an altered one.</para>
/// </summary>
public sealed class DesignAssistantMessage
{
    public Guid Id { get; set; }
    public Guid DesignId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>Order within the design's chat.</summary>
    public int Seq { get; set; }
    public string Role { get; set; } = default!;
    public string ContentJson { get; set; } = default!;
    /// <summary>True on the first entry of a person's message, where one exchange (and one undo step) starts.</summary>
    public bool StartsTurn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What one account's assistant use cost in a calendar month (UTC), from the token counts Claude
/// reports. The monthly allowance is checked against <see cref="CostMicros"/>.
/// </summary>
public sealed class AssistantUsage
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>yyyy-MM, UTC.</summary>
    public string Month { get; set; } = default!;
    public long InputTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long OutputTokens { get; set; }
    /// <summary>US dollars × 1,000,000.</summary>
    public long CostMicros { get; set; }
    public int Requests { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
