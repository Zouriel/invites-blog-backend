using System.Collections.Concurrent;
using System.Globalization;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Designs.AiPictures;

/// <summary>Settings (section <c>AiPictures</c>). Without an <see cref="ApiKey"/> the feature reports itself unavailable.</summary>
public sealed class AiPictureOptions
{
    public const string Section = "AiPictures";

    /// <summary>Ideogram API key (secrets file only).</summary>
    public string? ApiKey { get; set; }
    /// <summary>Pictures per account per calendar month (UTC).</summary>
    public int MonthlyLimit { get; set; } = 50;
    /// <summary>turbo | default | quality. Turbo is the cheapest and plenty for decorations.</summary>
    public string RenderingSpeed { get; set; } = "turbo";
    /// <summary>1k is sharp enough for a phone-sized invitation; the designer shrinks pictures further anyway.</summary>
    public string Resolution { get; set; } = "1k";
}

/// <param name="Allowed">An admin turned it on for this account.</param>
/// <param name="Available">Allowed, connected, and pictures left this month.</param>
/// <param name="Reason">Why not available: not_allowed | unavailable | limit, else null.</param>
public sealed record AiPictureStatusDto(bool Allowed, bool Available, string? Reason, int Used, int Limit, DateTimeOffset ResetsAt);

/// <param name="Shape">square | wide | strip | tall.</param>
public sealed record AiPictureRequest(string Prompt, string? Shape);

/// <summary>A picture made by an image model: a PNG with see-through background.</summary>
public sealed record GeneratedPicture(byte[] Content, string Prompt);

/// <summary>The image model behind "Create with AI" (Ideogram).</summary>
public interface IPictureGenerator
{
    bool Configured { get; }

    /// <param name="aspect">Ideogram's aspect ratio, e.g. "1x1".</param>
    Task<GeneratedPicture> GenerateAsync(string prompt, string aspect, CancellationToken ct);
}

public interface IAiPictureService
{
    Task<AiPictureStatusDto> StatusAsync(CancellationToken ct = default);

    /// <summary>Makes a picture with a see-through background and returns it ready to place, like library art.</summary>
    Task<ArtImportDto> GenerateAsync(AiPictureRequest request, CancellationToken ct = default);
}

/// <summary>
/// "Create with AI": a picture made from a description, with a see-through background (lace, flowers,
/// a wax seal…), placed like library art. Each one costs money, so only accounts an admin has allowed
/// (<see cref="AppUser.AiPicturesAllowed"/>) can use it, each with a monthly limit, one at a time.
/// </summary>
public sealed class AiPictureService(
    IRepository<AppUser> users,
    IRepository<AiPictureUsage> usage,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IPictureGenerator generator,
    IArtLibrary art,
    AiPictureOptions options,
    ILogger<AiPictureService> log) : IAiPictureService
{
    public const int MaxPromptLength = 400;

    /// <summary>One picture at a time per account: a double tap or a looping assistant can't run up the bill.</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Running = new();

    private static readonly Dictionary<string, string> Aspects = new()
    {
        ["square"] = "1x1", ["wide"] = "2x1", ["strip"] = "4x1", ["tall"] = "1x2",
    };

    public async Task<AiPictureStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var me = Me();
        var now = DateTimeOffset.UtcNow;
        var month = MonthOf(now);
        var resets = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
        var allowed = await users.Query().Where(u => u.Id == me).Select(u => u.AiPicturesAllowed).FirstOrDefaultAsync(ct);
        var used = await usage.Query().Where(u => u.UserId == me && u.Month == month).Select(u => u.Count).FirstOrDefaultAsync(ct);
        string? reason = !allowed ? "not_allowed" : !generator.Configured ? "unavailable" : used >= options.MonthlyLimit ? "limit" : null;
        return new AiPictureStatusDto(allowed, reason is null, reason, used, options.MonthlyLimit, resets);
    }

    public async Task<ArtImportDto> GenerateAsync(AiPictureRequest request, CancellationToken ct = default)
    {
        var prompt = (request.Prompt ?? string.Empty).Trim();
        if (prompt.Length == 0) throw new BusinessRuleException("Describe the picture you want.", "ai_picture_prompt");
        if (prompt.Length > MaxPromptLength) throw new BusinessRuleException($"Keep the description under {MaxPromptLength} characters.", "ai_picture_prompt");
        var aspect = Aspects.GetValueOrDefault(request.Shape ?? "square") ?? throw new BusinessRuleException("Pick square, wide, strip or tall.", "ai_picture_shape");

        var me = Me();
        var gate = Running.GetOrAdd(me, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct))
            throw new BusinessRuleException("A picture is already being made — wait for it to arrive.", "ai_picture_busy");
        try
        {
            var status = await StatusAsync(ct);
            if (!status.Available)
                throw status.Reason switch
                {
                    "not_allowed" => new ForbiddenException("Creating pictures with AI isn't turned on for this account.", "ai_picture_not_allowed"),
                    "limit" => new BusinessRuleException(
                        $"You've made this month's {status.Limit} AI pictures. More on {status.ResetsAt.ToString("d MMMM", CultureInfo.InvariantCulture)}.", "ai_picture_limit"),
                    _ => new InvalidStateException("Creating pictures with AI isn't available right now.", "ai_picture_unavailable"),
                };

            var picture = await generator.GenerateAsync(prompt, aspect, ct);
            await CountAsync(me, ct);
            log.LogInformation("AI picture made for {UserId} ({Aspect}, {Bytes} bytes)", me, aspect, picture.Content.Length);
            return art.ImportFile(picture.Content, Title(prompt) + ".png", "image/png",
                new ArtCreditDto("Ideogram", null, "Made with AI", null));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task CountAsync(Guid me, CancellationToken ct)
    {
        var month = MonthOf(DateTimeOffset.UtcNow);
        var row = await usage.Query(tracking: true).FirstOrDefaultAsync(u => u.UserId == me && u.Month == month, ct);
        if (row is null)
        {
            row = new AiPictureUsage { Id = Guid.NewGuid(), UserId = me, Month = month };
            await usage.AddAsync(row, ct);
        }
        row.Count++;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>A short name for the element and its file: the description's first few words.</summary>
    public static string Title(string prompt)
    {
        var words = prompt.Split((char[])[' ', '\n', '\r', '\t', ',', '.', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => char.IsLetterOrDigit(c) || c is '-' or '\'').ToArray()))
            .Where(w => w.Length > 0).Take(5);
        var title = string.Join(' ', words);
        if (title.Length > 40) title = title[..40].TrimEnd();
        return title.Length == 0 ? "AI picture" : char.ToUpperInvariant(title[0]) + title[1..];
    }

    private Guid Me() => currentUser.UserId ?? throw new UnauthorizedException("Sign in to create pictures.");

    private static string MonthOf(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}
