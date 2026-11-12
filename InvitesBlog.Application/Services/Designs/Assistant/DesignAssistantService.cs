using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Designs.Assistant;

public sealed record AssistantStatusDto(bool Allowed, string? Reason, decimal UsedUsd, decimal AllowanceUsd, DateTimeOffset ResetsAt);

public sealed record AssistantToolCallDto(string Id, string Name, JsonElement Input);

/// <summary>One chat entry as the browser shows it. Results entries carry one line per tool call.</summary>
public sealed record AssistantMessageDto(Guid Id, string Role, string Text, IReadOnlyList<string> Lines, bool StartsTurn, DateTimeOffset CreatedAt);

public sealed record AssistantChatDto(IReadOnlyList<AssistantMessageDto> Messages, AssistantStatusDto Status, IReadOnlyList<AssistantToolCallDto> PendingToolCalls);

/// <summary>What one step answers: the entries it added, the tool calls to run now, and whether the exchange is over.</summary>
public sealed record AssistantStepDto(IReadOnlyList<AssistantMessageDto> Messages, IReadOnlyList<AssistantToolCallDto> ToolCalls, bool Done, AssistantStatusDto Status);

public sealed record AssistantSendRequest(string Text, string Outline, AssistantVocabulary? Vocabulary);

/// <param name="Summary">One plain line for the person ("Added text “Names”"); the model gets <paramref name="Text"/>.</param>
/// <param name="Image">A picture the tool took (the `look` tool: a screen of the design), as a JPEG/PNG/WebP data: URI.</param>
public sealed record AssistantToolResult(string Id, bool Ok, string Text, string? Summary = null, string? Image = null);

public sealed record AssistantResultsRequest(IReadOnlyList<AssistantToolResult> Results, AssistantVocabulary? Vocabulary);

public interface IDesignAssistantService
{
    Task<AssistantChatDto> GetAsync(Guid designId, CancellationToken ct = default);
    Task<AssistantStepDto> SendAsync(Guid designId, AssistantSendRequest request, CancellationToken ct = default);
    Task<AssistantStepDto> ContinueAsync(Guid designId, AssistantResultsRequest request, CancellationToken ct = default);
    Task ClearAsync(Guid designId, CancellationToken ct = default);
}

/// <summary>
/// The designer's assistant: a chat per design in which Claude uses the designer's own tools. The
/// tools run in the person's browser, on the live editor, so the server never edits a scene; it holds
/// the conversation, talks to Claude, and keeps each account inside its plan and allowance.
///
/// <para>One message from the person is one exchange: Claude answers with text and tool calls, the
/// browser runs them and reports back (<see cref="ContinueAsync"/>), and so on until Claude stops or
/// the step limit is reached. Each step is a short request, and everything is stored as it happens,
/// so a dropped connection loses nothing.</para>
///
/// <para><b>Tokens.</b> The model sees a compact outline of the design (sent with the message), only
/// the last few exchanges (older ones without their outlines, thinking or long tool results), and a
/// system prompt and tool list that never change, so they come from the prompt cache.</para>
/// </summary>
public sealed partial class DesignAssistantService(
    ICurrentUser currentUser,
    IRepository<TemplateDesign> designs,
    IRepository<DesignAssistantMessage> messages,
    IRepository<AssistantUsage> usage,
    IRepository<AppUser> users,
    IAssistantModel model,
    AssistantOptions options,
    IUnitOfWork uow,
    ILogger<DesignAssistantService> log) : IDesignAssistantService
{
    public const int MaxTextLength = 2000;
    public const int MaxOutlineLength = 24_000;
    public const int MaxResultLength = 6000;
    /// <summary>Older tool results are cut to this when they are sent again.</summary>
    public const int OldResultLength = 160;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private AssistantOptions Options => options;

    public async Task<AssistantChatDto> GetAsync(Guid designId, CancellationToken ct = default)
    {
        await LoadOwnedAsync(designId, ct);
        var rows = await ChatAsync(designId, ct);
        return new AssistantChatDto(rows.Select(ToDto).Where(m => m is not null).Select(m => m!).ToList(), await StatusAsync(ct), Pending(rows));
    }

    public async Task ClearAsync(Guid designId, CancellationToken ct = default)
    {
        await LoadOwnedAsync(designId, ct);
        var rows = await messages.Query(tracking: true).Where(m => m.DesignId == designId).ToListAsync(ct);
        messages.RemoveRange(rows);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<AssistantStepDto> SendAsync(Guid designId, AssistantSendRequest request, CancellationToken ct = default)
    {
        var me = Me();
        await LoadOwnedAsync(designId, ct);
        var text = (request.Text ?? "").Trim();
        if (text.Length == 0) throw new BusinessRuleException("Write a message first.", "assistant_empty");
        if (text.Length > MaxTextLength) throw new BusinessRuleException($"Keep a message under {MaxTextLength} characters.", "assistant_too_long");
        var outline = (request.Outline ?? "").Trim();
        if (outline.Length > MaxOutlineLength) outline = outline[..MaxOutlineLength] + "\n… (outline cut; use get_elements for the rest)";
        await RequireAllowedAsync(ct);

        var rows = (await ChatAsync(designId, ct)).ToList();
        var added = new List<DesignAssistantMessage>();

        // A turn left unfinished (the tab closed mid-run): answer its open tool calls so the history stays valid.
        if (Pending(rows) is { Count: > 0 } open)
            added.Add(await AppendAsync(designId, me, rows, "results", open.Select(c =>
                new AssistantBlock("tool_result", "Not run: the person sent a new message first.", c.Id, IsError: true)).ToList(), false, ct));

        added.Add(await AppendAsync(designId, me, rows, "user",
            [new AssistantBlock("context", $"<design>\n{outline}\n</design>"), new AssistantBlock("text", text)], true, ct));
        await uow.SaveChangesAsync(ct);

        return await StepAsync(designId, me, rows, added, request.Vocabulary, ct);
    }

    public async Task<AssistantStepDto> ContinueAsync(Guid designId, AssistantResultsRequest request, CancellationToken ct = default)
    {
        var me = Me();
        await LoadOwnedAsync(designId, ct);
        var rows = (await ChatAsync(designId, ct)).ToList();
        var open = Pending(rows);
        if (open.Count == 0) throw new InvalidStateException("There's nothing waiting for results.", "assistant_nothing_pending");

        var given = (request.Results ?? []).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        var blocks = open.Select(c => given.TryGetValue(c.Id, out var r)
            ? new AssistantBlock("tool_result", Clip(r.Text ?? "", MaxResultLength), c.Id, IsError: r.Ok ? null : true,
                Summary: r.Ok && !string.IsNullOrWhiteSpace(r.Summary) ? Clip(r.Summary.Trim(), 120) : null,
                Data: r.Ok ? Picture(r.Image) : null)
            : new AssistantBlock("tool_result", "Not run.", c.Id, IsError: true)).ToList();
        var added = new List<DesignAssistantMessage> { await AppendAsync(designId, me, rows, "results", blocks, false, ct) };
        await uow.SaveChangesAsync(ct);

        // Over the allowance or the step limit: stop here, saying so, instead of asking Claude again.
        var steps = rows.Skip(Math.Max(0, rows.FindLastIndex(r => r.StartsTurn))).Count(r => r.Role == "assistant");
        var status = await StatusAsync(ct);
        string? stop = !status.Allowed ? StopNote(status)
            : steps >= Options.MaxStepsPerMessage ? "I've stopped here so this message doesn't run on too long. Tell me to carry on if you'd like more." : null;
        if (stop is not null)
        {
            added.Add(await AppendAsync(designId, me, rows, "assistant", [new AssistantBlock("text", stop)], false, ct));
            await uow.SaveChangesAsync(ct);
            return new AssistantStepDto(Dtos(added), [], true, status);
        }

        return await StepAsync(designId, me, rows, added, request.Vocabulary, ct);
    }

    // ----- One call to Claude -------------------------------------------------------------------

    private async Task<AssistantStepDto> StepAsync(Guid designId, Guid me, List<DesignAssistantMessage> rows, List<DesignAssistantMessage> added, AssistantVocabulary? vocabulary, CancellationToken ct)
    {
        AssistantModelReply reply;
        try
        {
            var (modelId, effort) = Options.ModelFor(me);
            reply = await model.CompleteAsync(new AssistantModelRequest(Window(rows), Clean(vocabulary), modelId, effort), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Designer assistant: the model call failed for design {DesignId}", designId);
            added.Add(await AppendAsync(designId, me, rows, "assistant",
                [new AssistantBlock("text", "Sorry, I couldn't reach the assistant just now. Please try again in a moment.")], false, ct));
            await uow.SaveChangesAsync(ct);
            return new AssistantStepDto(Dtos(added), [], true, await StatusAsync(ct));
        }

        await RecordUsageAsync(me, Options.ModelFor(me).Model, reply.Usage, ct);

        var blocks = reply.Blocks.ToList();
        if (reply.StopReason == "refusal")
            blocks = [new AssistantBlock("text", "I can't help with that one. Try asking for a different change to the design.")];
        else if (reply.StopReason == "max_tokens")
        {
            // A tool call cut off mid-way can't be run; drop it and say so.
            while (blocks.Count > 0 && blocks[^1].Type == "tool_use") blocks.RemoveAt(blocks.Count - 1);
            if (!blocks.Any(b => b.Type == "tool_use"))
                blocks.Add(new AssistantBlock("text", "That was more than I could do in one go. Ask for it in smaller steps."));
        }
        if (!blocks.Any(b => b.Type is "text" or "tool_use"))
            blocks.Add(new AssistantBlock("text", "Done."));

        added.Add(await AppendAsync(designId, me, rows, "assistant", blocks, false, ct));
        await uow.SaveChangesAsync(ct);

        var calls = Pending(rows);
        return new AssistantStepDto(Dtos(added), calls, calls.Count == 0, await StatusAsync(ct));
    }

    /// <summary>
    /// What the model sees: the current exchange in full, and up to <see cref="AssistantOptions.HistoryTurns"/>
    /// earlier ones without their outlines and thinking, their tool results cut short. Built the same way
    /// on every step of an exchange, so within it the history only grows (the prompt cache and Claude's
    /// thinking signatures both rely on that).
    /// </summary>
    public List<AssistantTurnMessage> Window(IReadOnlyList<DesignAssistantMessage> rows)
    {
        var starts = rows.Select((r, i) => (r, i)).Where(x => x.r.StartsTurn).Select(x => x.i).ToList();
        if (starts.Count == 0) return [];
        var current = starts[^1];
        var first = starts[Math.Max(0, starts.Count - 1 - Options.HistoryTurns)];

        var result = new List<AssistantTurnMessage>();
        for (var i = first; i < rows.Count; i++)
        {
            var old = i < current;
            var blocks = Blocks(rows[i])
                .Where(b => !(old && b.Type is "thinking" or "redacted_thinking" or "context"))
                // A picture is for the turn it was taken in; later turns keep only that one was shown.
                .Select(b => old && b.Type == "tool_result"
                    ? b with { Text = Clip(b.Text ?? "", OldResultLength) + (b.Data is null ? "" : " (a picture was shown)"), Data = null }
                    : b)
                .ToList();
            if (blocks.Count == 0) continue;
            var role = rows[i].Role == "assistant" ? "assistant" : "user";
            // Two entries from the same side in a row (an unanswered call answered on the next message) are one message.
            if (result.Count > 0 && result[^1].Role == role)
                result[^1] = result[^1] with { Blocks = [.. result[^1].Blocks, .. blocks] };
            else
                result.Add(new AssistantTurnMessage(role, blocks));
        }
        return result;
    }

    // ----- Access and allowance -----------------------------------------------------------------

    private async Task RequireAllowedAsync(CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (status.Allowed) return;
        throw status.Reason switch
        {
            "premium" => new ForbiddenException("The design assistant comes with Premium.", "assistant_premium_only"),
            "allowance" => new BusinessRuleException(StopNote(status), "assistant_allowance_used"),
            _ => new InvalidStateException("The design assistant isn't available right now.", "assistant_unavailable"),
        };
    }

    private async Task<AssistantStatusDto> StatusAsync(CancellationToken ct)
    {
        var me = Me();
        var now = DateTimeOffset.UtcNow;
        var month = MonthOf(now);
        var resets = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var used = await usage.Query().Where(u => u.UserId == me && u.Month == month).Select(u => u.CostMicros).FirstOrDefaultAsync(ct);
        var usedUsd = Math.Round(used / 1_000_000m, 2);

        string? reason = null;
        if (!await EligibleAsync(me, now, ct)) reason = "premium";
        else if (!model.Configured) reason = "unavailable";
        // The owner has no allowance; their use is still recorded.
        else if (!Options.IsOwner(me) && used >= (long)(Options.MonthlyAllowanceUsd * 1_000_000m)) reason = "allowance";
        // No allowance (0) for the owner, so the panel never shows them a "% used" note.
        var allowance = Options.IsOwner(me) ? 0m : Options.MonthlyAllowanceUsd;
        return new AssistantStatusDto(reason is null, reason, usedUsd, allowance, resets);
    }

    /// <summary>Premium or Studio (Venue) in force, or an admin (who supports both).</summary>
    private async Task<bool> EligibleAsync(Guid me, DateTimeOffset now, CancellationToken ct)
    {
        if (currentUser.HasPermission(Permissions.Templates.Manage)) return true;
        var user = await users.GetByIdAsync(me, ct);
        return user is { SubscriptionTier: SubscriptionTier.Premium or SubscriptionTier.Venue }
            && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
    }

    private static string StopNote(AssistantStatusDto status) => status.Reason == "allowance"
        ? $"You've used this month's AI allowance. It resets on {status.ResetsAt.ToString("d MMMM", CultureInfo.InvariantCulture)}."
        : "The design assistant isn't available on this account.";

    private async Task RecordUsageAsync(Guid me, string modelId, AssistantUsageCount count, CancellationToken ct)
    {
        var month = MonthOf(DateTimeOffset.UtcNow);
        var row = await usage.Query(tracking: true).FirstOrDefaultAsync(u => u.UserId == me && u.Month == month, ct);
        if (row is null)
        {
            row = new AssistantUsage { Id = Guid.NewGuid(), UserId = me, Month = month };
            await usage.AddAsync(row, ct);
        }
        row.InputTokens += count.InputTokens;
        row.CacheWriteTokens += count.CacheWriteTokens;
        row.CacheReadTokens += count.CacheReadTokens;
        row.OutputTokens += count.OutputTokens;
        row.CostMicros += AssistantOptions.CostMicros(modelId, count);
        row.Requests++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    private static string MonthOf(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    // ----- Storage ------------------------------------------------------------------------------

    private async Task<IReadOnlyList<DesignAssistantMessage>> ChatAsync(Guid designId, CancellationToken ct) =>
        await messages.Query().Where(m => m.DesignId == designId).OrderBy(m => m.Seq).ToListAsync(ct);

    private async Task<DesignAssistantMessage> AppendAsync(Guid designId, Guid me, List<DesignAssistantMessage> rows, string role, IReadOnlyList<AssistantBlock> blocks, bool startsTurn, CancellationToken ct)
    {
        var row = new DesignAssistantMessage
        {
            Id = Guid.NewGuid(), DesignId = designId, UserId = me, Seq = rows.Count == 0 ? 1 : rows[^1].Seq + 1,
            Role = role, ContentJson = JsonSerializer.Serialize(blocks, Json), StartsTurn = startsTurn, CreatedAt = DateTimeOffset.UtcNow,
        };
        await messages.AddAsync(row, ct);
        rows.Add(row);
        return row;
    }

    private static IReadOnlyList<AssistantBlock> Blocks(DesignAssistantMessage row) =>
        JsonSerializer.Deserialize<List<AssistantBlock>>(row.ContentJson, Json) ?? [];

    /// <summary>Tool calls in the last entry that haven't been answered yet.</summary>
    private static IReadOnlyList<AssistantToolCallDto> Pending(IReadOnlyList<DesignAssistantMessage> rows)
    {
        if (rows.Count == 0 || rows[^1].Role != "assistant") return [];
        return Blocks(rows[^1]).Where(b => b.Type == "tool_use" && b.Id is not null && b.Name is not null)
            .Select(b => new AssistantToolCallDto(b.Id!, b.Name!, b.Input ?? JsonDocument.Parse("{}").RootElement.Clone())).ToList();
    }

    private static IReadOnlyList<AssistantMessageDto> Dtos(IEnumerable<DesignAssistantMessage> rows) =>
        rows.Select(ToDto).Where(m => m is not null).Select(m => m!).ToList();

    private static AssistantMessageDto? ToDto(DesignAssistantMessage row)
    {
        var blocks = Blocks(row);
        var text = string.Join("\n\n", blocks.Where(b => b.Type == "text").Select(b => b.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        // The person sees the plain lines of what worked; the details are for the model.
        var lines = row.Role == "results" ? blocks.Where(b => b.Type == "tool_result" && b.Summary is not null).Select(b => b.Summary!).ToList() : [];
        if (text.Length == 0 && lines.Count == 0) return null;
        return new AssistantMessageDto(row.Id, row.Role, text, lines, row.StartsTurn, row.CreatedAt);
    }

    /// <summary>The largest picture a tool may hand back (a screen at about half size is ~40 KB).</summary>
    public const int MaxPictureChars = 400_000;

    /// <summary>A tool's picture when it really is one (an image data: URI of a sensible size); null otherwise.</summary>
    public static string? Picture(string? image) =>
        image is { Length: > 30 and <= MaxPictureChars }
        && System.Text.RegularExpressions.Regex.IsMatch(image, "^data:image/(jpeg|png|webp);base64,[A-Za-z0-9+/=]+$")
            ? image : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ----- Helpers ------------------------------------------------------------------------------

    private Guid Me() => currentUser.UserId ?? throw new UnauthorizedException("Sign in to use the design assistant.");

    private async Task LoadOwnedAsync(Guid id, CancellationToken ct)
    {
        var me = Me();
        if (!await designs.AnyAsync(d => d.Id == id && d.OwnerUserId == me, ct))
            throw new NotFoundException("That design doesn't exist.", "design_not_found");
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex OptionId();

    /// <summary>The browser's names, kept to short ids and labels: they go into the system prompt.</summary>
    public static AssistantVocabulary Clean(AssistantVocabulary? v)
    {
        static IReadOnlyList<AssistantOption> Keep(IReadOnlyList<AssistantOption>? list) => (list ?? [])
            .Where(o => o is not null && OptionId().IsMatch(o.Id ?? ""))
            .Select(o => new AssistantOption(o.Id, Regex.Replace(o.Label ?? "", @"[^\p{L}\p{N} '&,\-]", "").Trim() is { Length: > 0 } l ? (l.Length > 40 ? l[..40] : l) : o.Id))
            .DistinctBy(o => o.Id).Take(60).OrderBy(o => o.Id, StringComparer.Ordinal).ToList();
        return new AssistantVocabulary(Keep(v?.Moves), Keep(v?.Stickers));
    }
}
