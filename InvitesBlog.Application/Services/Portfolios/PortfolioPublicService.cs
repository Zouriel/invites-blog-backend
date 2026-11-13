using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Push;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Portfolios;

/// <summary>The public side of a Studio portfolio: the page's data, enquiries, and "Ask to remove a photo". No account needed.</summary>
public interface IPortfolioPublicService
{
    /// <summary>
    /// The portfolio at this handle, or where a changed handle now leads. Null portfolio and null redirect: not available
    /// (no such handle, unpublished, or the Studio has ended). <paramref name="previewPortfolioId"/> (from a signed preview
    /// link) shows that portfolio even unpublished, uncached.
    /// </summary>
    Task<PortfolioLookup> FindAsync(string? handle, Guid? previewPortfolioId = null, CancellationToken ct = default);

    Task<PortfolioOkDto> EnquireAsync(string? handle, PortfolioEnquiryRequest request, CancellationToken ct = default);
    Task<PortfolioOkDto> ReportAsync(string? handle, PortfolioReportRequest request, CancellationToken ct = default);

    /// <summary>
    /// Every portfolio a visitor can open now (published, with a handle, its owner active and on Studio or Photographer),
    /// for the search engines' sitemap: handle and when it last changed.
    /// </summary>
    Task<IReadOnlyList<PortfolioSitemapEntry>> PublishedAsync(CancellationToken ct = default);
}

/// <summary>One public portfolio in the sitemap.</summary>
public sealed record PortfolioSitemapEntry(string Handle, DateTimeOffset UpdatedAt);

public static class PortfolioReportKinds
{
    /// <summary>One photo on a portfolio (target = the portfolio photo's id).</summary>
    public const string Photo = "portfolio_photo";
    /// <summary>The page as a whole, when no photo was named (target = the portfolio's id).</summary>
    public const string Page = "portfolio";
}

public sealed partial class PortfolioPublicService(
    IRepository<Portfolio> portfolios,
    IRepository<PortfolioSection> sections,
    IRepository<PortfolioCollection> collections,
    IRepository<PortfolioPhoto> photos,
    IRepository<PortfolioHandleRedirect> redirects,
    IRepository<PortfolioEnquiry> enquiries,
    IRepository<Template> templates,
    IRepository<AppUser> users,
    IRepository<ContentReport> reports,
    IPushQueue push,
    IEmailSender email,
    IReportMailService reportMail,
    IConfiguration config,
    IUnitOfWork uow,
    ILogger<PortfolioPublicService> logger) : IPortfolioPublicService
{
    /// <summary>How long a public answer is reused. Short: a studio checking a change sees it within a minute.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(45);

    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, PortfolioLookup Value)> Cache = new();

    public async Task<IReadOnlyList<PortfolioSitemapEntry>> PublishedAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await portfolios.Query()
            .Where(p => p.IsPublished && p.Handle != null)
            .Join(users.Query(), p => p.UserId, u => u.Id, (p, u) => new { p.Handle, p.UpdatedAt, User = u })
            .Where(x => x.User.IsActive
                        && (x.User.SubscriptionTier == SubscriptionTier.Venue || x.User.SubscriptionTier == SubscriptionTier.Photographer))
            .ToListAsync(ct);
        // The same test the page itself makes (StudioActive), so the sitemap never lists a page that says "not available".
        return rows.Where(x => AddOns.StudioActive(x.User, now))
            .Select(x => new PortfolioSitemapEntry(x.Handle!, x.UpdatedAt))
            .OrderBy(x => x.Handle, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Drops every cached answer (tests; a studio's own edits simply wait out <see cref="CacheFor"/>).</summary>
    public static void ClearCache() => Cache.Clear();

    public static readonly PortfolioLookup Unavailable = new(null, null);

    public async Task<PortfolioLookup> FindAsync(string? raw, Guid? previewPortfolioId = null, CancellationToken ct = default)
    {
        var handle = PortfolioHandles.Normalize(raw);
        if (PortfolioHandles.ShapeProblem(handle) is not null) return Unavailable;
        var now = DateTimeOffset.UtcNow;
        if (previewPortfolioId is null && Cache.TryGetValue(handle, out var hit) && now - hit.At < CacheFor) return hit.Value;

        var result = await LoadAsync(handle, previewPortfolioId, now, ct);
        if (previewPortfolioId is null)
        {
            if (Cache.Count > 5000) Cache.Clear();
            Cache[handle] = (now, result);
        }
        return result;
    }

    private async Task<PortfolioLookup> LoadAsync(string handle, Guid? preview, DateTimeOffset now, CancellationToken ct)
    {
        var p = await portfolios.Query().FirstOrDefaultAsync(x => x.Handle == handle, ct);
        if (p is null)
        {
            var moved = await redirects.Query().FirstOrDefaultAsync(x => x.Handle == handle && x.ExpiresAt > now, ct);
            if (moved is null) return Unavailable;
            var target = await portfolios.Query().FirstOrDefaultAsync(x => x.Id == moved.PortfolioId, ct);
            return target?.Handle is { } h && h != handle ? new PortfolioLookup(null, h) : Unavailable;
        }
        var isPreview = preview == p.Id;
        if (!isPreview && !p.IsPublished) return Unavailable;
        var owner = await users.GetByIdAsync(p.UserId, ct);
        if (owner is null || !owner.IsActive || (!isPreview && !AddOns.StudioActive(owner, now))) return Unavailable;
        return new PortfolioLookup(await BuildAsync(p, ct), null);
    }

    private async Task<PublicPortfolioDto> BuildAsync(Portfolio p, CancellationToken ct)
    {
        var sectionRows = await sections.Query().Where(x => x.PortfolioId == p.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var collectionRows = await collections.Query().Where(x => x.PortfolioId == p.Id).ToListAsync(ct);
        var photoRows = await photos.Query().Where(x => x.PortfolioId == p.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var cols = PortfolioService.Collections(sectionRows, collectionRows, photoRows).ToDictionary(c => c.Id);
        var byId = photoRows.ToDictionary(x => x.Id);
        var coverUrl = PortfolioService.CoverUrl(p, [.. cols.Values], photoRows);
        var designs = await PortfolioService.PublishedDesigns(templates.Query(), p.UserId)
            .Select(t => new PortfolioDesignDto(t.Id, t.Name, t.PreviewImageUrl)).ToListAsync(ct);
        var contact = new PortfolioContactDto(p.WhatsApp, p.Viber, p.Instagram, p.Phone, p.Email, p.EnquiriesOn);
        var hasContact = p.EnquiriesOn || new[] { p.WhatsApp, p.Viber, p.Instagram, p.Phone, p.Email }.Any(v => !string.IsNullOrWhiteSpace(v));

        var list = new List<PublicPortfolioSectionDto>();
        foreach (var s in sectionRows)
        {
            if (s.Hidden && s.Kind != PortfolioSectionKinds.Cover) continue;
            var d = PortfolioService.Read(s.DataJson);
            string? photoUrl = d.PhotoId is { } pid && byId.TryGetValue(pid, out var ph) ? ph.Url : null;
            switch (s.Kind)
            {
                case PortfolioSectionKinds.Cover:
                    list.Add(new PublicPortfolioSectionDto(s.Kind, null, null, coverUrl, null, null, null, null));
                    break;
                case PortfolioSectionKinds.Collection:
                    if (s.CollectionId is { } cid && cols.TryGetValue(cid, out var c) && c.Photos.Count > 0)
                        list.Add(new PublicPortfolioSectionDto(s.Kind, c.Title, null, null,
                            new PublicCollectionDto(c.Id, c.Title, c.Layout, c.CoverUrl, c.Photos, c.Kind), null, null, null));
                    break;
                case PortfolioSectionKinds.About:
                    if (!string.IsNullOrWhiteSpace(d.Body) || photoUrl is not null)
                        list.Add(new PublicPortfolioSectionDto(s.Kind, d.Title ?? "About", d.Body, photoUrl, null, null, null, null));
                    break;
                case PortfolioSectionKinds.Designs:
                    var chosen = d.TemplateIds is { Count: > 0 } ids ? designs.Where(x => ids.Contains(x.Id)).ToList() : designs;
                    if (chosen.Count > 0)
                        list.Add(new PublicPortfolioSectionDto(s.Kind, d.Title ?? "Invitation designs", null, null, null, chosen, null, null));
                    break;
                case PortfolioSectionKinds.Packages:
                    if (d.Packages is { Count: > 0 })
                        list.Add(new PublicPortfolioSectionDto(s.Kind, d.Title ?? "Packages", null, null, null, null, d.Packages, null));
                    break;
                case PortfolioSectionKinds.Reviews:
                    if (d.Reviews is { Count: > 0 })
                        list.Add(new PublicPortfolioSectionDto(s.Kind, d.Title ?? "Kind words", null, null, null, null, null, d.Reviews));
                    break;
                case PortfolioSectionKinds.Contact:
                    if (hasContact)
                        list.Add(new PublicPortfolioSectionDto(s.Kind, d.Title ?? "Get in touch", null, null, null, null, null, null));
                    break;
            }
        }
        return new PublicPortfolioDto(p.Handle!, $"{config.InviterBase()}/@{p.Handle}", p.DisplayName, p.Tagline, p.LogoUrl, coverUrl,
            PortfolioChoices.Theme(p.Theme).Key, p.Accent, PortfolioChoices.Font(p.Font).Key, contact, list,
            PortfolioChoices.LogoPosition(p.LogoPosition), PortfolioChoices.LogoSize(p.LogoSize));
    }

    // ---------- enquiries ----------

    [GeneratedRegex(@"https?://|www\.", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    public async Task<PortfolioOkDto> EnquireAsync(string? handle, PortfolioEnquiryRequest r, CancellationToken ct = default)
    {
        const string thanks = "Thank you. Your message was sent; the studio will get back to you.";
        var p = await PublishedAsync(handle, ct);
        if (!p.EnquiriesOn) throw new CodedRuleException("This studio isn't taking messages here. Use the buttons to contact them.", "enquiries_off");
        // A filled honeypot is a bot: it is told it worked and nothing is kept or sent.
        if (!string.IsNullOrWhiteSpace(r.Website)) return new PortfolioOkDto(true, thanks);

        var name = PortfolioService.Clean(r.Name, 80);
        var contact = PortfolioService.Clean(r.Contact, 120);
        var message = PortfolioService.Clean(r.Message, 2000, multiline: true);
        var date = PortfolioService.Clean(r.EventDate, 40);
        if (name is null) throw new CodedRuleException("Add your name.", "name_required");
        if (contact is null || contact.Length < 5) throw new CodedRuleException("Add a phone number or email so they can reply.", "contact_required");
        if (message is null || message.Length < 2) throw new CodedRuleException("Write a short message.", "message_required");
        if (Link().Matches(message).Count > 2 || Link().IsMatch(name))
            throw new CodedRuleException("Please send your message without links.", "spam");
        if (ObjectionableText.IsObjectionable(message) || ObjectionableText.IsObjectionable(name))
            throw new CodedRuleException(ObjectionableText.Refusal, ObjectionableText.Code);

        var now = DateTimeOffset.UtcNow;
        // The same message twice (a double tap, a retry) is one enquiry.
        var since = now.AddMinutes(-10);
        if (await enquiries.AnyAsync(x => x.PortfolioId == p.Id && x.Contact == contact && x.Message == message && x.CreatedAt > since, ct))
            return new PortfolioOkDto(true, thanks);
        // A flood across many addresses still can't bury the studio: past this many a day, they are dropped.
        var day = now.AddDays(-1);
        if (await enquiries.Query().CountAsync(x => x.PortfolioId == p.Id && x.CreatedAt > day, ct) >= 100)
            return new PortfolioOkDto(true, thanks);

        var row = new PortfolioEnquiry
        {
            Id = Guid.NewGuid(), PortfolioId = p.Id, Name = name, Contact = contact, EventDate = date, Message = message, CreatedAt = now,
        };
        await enquiries.AddAsync(row, ct);
        await uowSave(ct);
        push.Enqueue(new PortfolioEnquiryReceived(row.Id));
        await MailStudioAsync(p, $"New enquiry from {name}",
            [("Name", name), ("Contact", contact), ("Event date", date), ("Message", message)],
            "See all your enquiries", $"{config.AppBase()}/portfolio/enquiries",
            contact.Contains('@') ? contact : null, ct);
        return new PortfolioOkDto(true, thanks);
    }

    // ---------- "Ask to remove a photo" ----------

    public static readonly string[] Reasons = ["me", "not_agreed", "other"];

    public async Task<PortfolioOkDto> ReportAsync(string? handle, PortfolioReportRequest r, CancellationToken ct = default)
    {
        const string done = "Thank you. We've told the studio and we'll look at it within 24 hours.";
        var p = await PublishedAsync(handle, ct);
        PortfolioPhoto? photo = null;
        if (r.PhotoId is { } pid)
            photo = await photos.Query().FirstOrDefaultAsync(x => x.Id == pid && x.PortfolioId == p.Id, ct)
                    ?? throw new NotFoundException("That photo isn't on this page any more.", "photo_not_found");
        var reason = Reasons.Contains(r.Reason) ? r.Reason! : "other";
        var note = PortfolioService.Clean(r.Note, 800, multiline: true);
        var contact = PortfolioService.Clean(r.Contact, 120);
        if (photo is null && note is null)
            throw new CodedRuleException("Tap the photo you mean, or describe it.", "photo_required");
        var kind = photo is null ? PortfolioReportKinds.Page : PortfolioReportKinds.Photo;
        var target = photo?.Id ?? p.Id;

        // Every request is recorded (the queue groups them by photo, and each may carry its own details and contact);
        // the studio is emailed once per open photo/page so repeats don't flood it. Rate limited per visitor.
        var firstOpen = !await reports.AnyAsync(x => x.Kind == kind && x.TargetId == target && x.Status == TemplateReportStatus.Open, ct);
        await reports.AddAsync(new ContentReport
        {
            Id = Guid.NewGuid(), ReporterUserId = null, Kind = kind, TargetId = target, CampaignId = photo?.CampaignId,
            SubjectUserId = p.UserId, Reason = reason,
            Note = string.Join("\n", new[] { note, contact is null ? null : $"Contact: {contact}" }.OfType<string>()) is { Length: > 0 } n ? n[..Math.Min(n.Length, 1000)] : null,
            Snapshot = $"@{p.Handle}" + (photo is null ? "" : $" photo {photo.ThumbUrl}"),
            Status = TemplateReportStatus.Open, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await uowSave(ct);
        await reportMail.SendAsync(photo is null ? "asked to remove a photo from a portfolio" : "asked to remove a portfolio photo",
            [("Portfolio", $"{config.InviterBase()}/@{p.Handle}"), ("Photo", photo is null ? null : Absolute(photo.Url)), ("Reason", ReasonText(reason)), ("Note", note), ("Contact", contact)], ct);
        if (firstOpen)
            await MailStudioAsync(p, "Someone asked you to remove a photo",
                [("Photo", photo is null ? "(not named; see the note)" : Absolute(photo.Url)), ("Reason", ReasonText(reason)), ("Note", note), ("Their contact", contact)],
                "Open your portfolio", $"{config.AppBase()}/portfolio", null, ct);
        return new PortfolioOkDto(true, done);
    }

    /// <summary>Stored pictures are site-relative (/assets/...): an email needs the whole address.</summary>
    private string Absolute(string url) =>
        url.StartsWith('/') ? config.InviterBase().TrimEnd('/') + url : url;

    public static string ReasonText(string reason) => reason switch
    {
        "me" => "I'm in this photo and want it removed",
        "not_agreed" => "We didn't agree to this being shown",
        _ => "Something else",
    };

    // ---------- helpers ----------

    /// <summary>The live (published, Studio in force) portfolio at a handle, or 404 portfolio_unavailable.</summary>
    private async Task<Portfolio> PublishedAsync(string? raw, CancellationToken ct)
    {
        var handle = PortfolioHandles.Normalize(raw);
        var p = await portfolios.Query().FirstOrDefaultAsync(x => x.Handle == handle && x.IsPublished, ct);
        var owner = p is null ? null : await users.GetByIdAsync(p.UserId, ct);
        if (p is null || owner is null || !owner.IsActive || !AddOns.StudioActive(owner, DateTimeOffset.UtcNow))
            throw new NotFoundException("This portfolio isn't available.", "portfolio_unavailable");
        return p;
    }

    private async Task MailStudioAsync(Portfolio p, string subject, IReadOnlyList<(string Label, string? Value)> lines,
        string linkText, string link, string? replyTo, CancellationToken ct)
    {
        var to = p.Email;
        if (string.IsNullOrWhiteSpace(to)) to = (await users.GetByIdAsync(p.UserId, ct))?.Email;
        if (string.IsNullOrWhiteSpace(to)) return;
        var rows = string.Concat(lines.Where(l => !string.IsNullOrWhiteSpace(l.Value)).Select(l =>
            $"<p style=\"margin:0 0 12px\"><strong>{WebUtility.HtmlEncode(l.Label)}:</strong><br>{WebUtility.HtmlEncode(l.Value).Replace("\n", "<br>")}</p>"));
        var html = $"<div style=\"font-family:sans-serif;max-width:520px\"><h2 style=\"font-weight:600\">{WebUtility.HtmlEncode(subject)}</h2>"
                   + $"<p style=\"color:#555\">From your portfolio, invites.blog/@{WebUtility.HtmlEncode(p.Handle)}</p>{rows}"
                   + $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(linkText)}</a></p></div>";
        try
        {
            await email.SendAsync(new EmailMessage(to.Trim(), subject, html, ReplyTo: replyTo,
                Tags: [new KeyValuePair<string, string>("kind", "portfolio")]), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Portfolio email to the studio failed");
        }
    }

    private Task uowSave(CancellationToken ct) => uow.SaveChangesAsync(ct);
}
