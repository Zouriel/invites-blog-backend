using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.MediaBuckets;

/// <summary>
/// Whether an event's photo window has ENDED, and when. <c>Closed</c> is false before and during the
/// window; <c>ClosesAt</c> is null for a save the date (no album) or an event with no real date.
/// </summary>
public sealed record PhotoWindowState(bool Closed, DateTimeOffset? ClosesAt)
{
    public static readonly PhotoWindowState None = new(false, null);
}

/// <summary>
/// The event-wide answer to "has the photo window closed", which decides whether new QR codes and links
/// (the open link, hand-sent and re-sent invitation links, sending the invitation at all) may still be made.
///
/// <para><b>The event's window is its longest album's.</b> Albums can collect for different numbers of days,
/// and while any of them still takes photos the event is not over. An event with no album yet answers with the
/// window its first album would be given (the plan's days), the same number <c>WindowForCampaignAsync</c> uses.
/// Every boundary is <see cref="EventDayWindow"/>'s, so this can never disagree with the camera or the upload.</para>
///
/// <para>Nothing here touches what already exists: printed codes and sent links keep opening their closed,
/// view-only page. Lengthening an album's window or moving the date opens everything again.</para>
/// </summary>
public interface IPhotoWindowService
{
    Task<PhotoWindowState> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>The same for a page of events, with one grouped query for their albums.</summary>
    Task<IReadOnlyDictionary<Guid, PhotoWindowState>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, CancellationToken ct = default);

    /// <summary>Throws the 409 <c>window_closed</c> refusal once the event's window has ended.</summary>
    Task EnsureNotClosedAsync(Guid campaignId, CancellationToken ct = default);
}

public sealed class PhotoWindowService(
    ICampaignRepository campaigns,
    IRepository<MediaBucket> buckets,
    IPlanService plans) : IPhotoWindowService
{
    public async Task<PhotoWindowState> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        if (campaign is null) return PhotoWindowState.None;
        return (await ForCampaignsAsync([campaign], ct)).GetValueOrDefault(campaignId, PhotoWindowState.None);
    }

    public async Task<IReadOnlyDictionary<Guid, PhotoWindowState>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, PhotoWindowState>(list.Count);
        if (list.Count == 0) return result;

        var ids = list.Select(c => c.Id).ToList();
        var longest = await buckets.Query()
            .Where(b => ids.Contains(b.CampaignId))
            .GroupBy(b => b.CampaignId)
            .Select(g => new { CampaignId = g.Key, Days = g.Max(b => b.UploadWindowDays) })
            .ToDictionaryAsync(x => x.CampaignId, x => x.Days, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var c in list)
        {
            // A save the date has no album, so it has no photo window to close.
            if (c.Kind == CampaignKind.SaveTheDate) { result[c.Id] = PhotoWindowState.None; continue; }

            var days = longest.TryGetValue(c.Id, out var d)
                ? d
                : (await plans.ForCampaignAsync(c.Id, ct)).MaxWindowDays;
            var closes = EventDayWindow.ClosesAt(c.EventStartAt, days, c.TimeZone);
            result[c.Id] = new PhotoWindowState(closes is { } at && now >= at, closes);
        }
        return result;
    }

    public async Task EnsureNotClosedAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        if (campaign is null) return;   // the caller's own lookup says "not found" in its own words
        var state = (await ForCampaignsAsync([campaign], ct)).GetValueOrDefault(campaignId, PhotoWindowState.None);
        if (state is { Closed: true, ClosesAt: { } closes })
            throw EventDayWindow.Closed(closes, campaign.TimeZone);
    }
}
