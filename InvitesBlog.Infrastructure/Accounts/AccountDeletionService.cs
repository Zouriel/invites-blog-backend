using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Accounts;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Security;

namespace InvitesBlog.Infrastructure.Accounts;

/// <summary>
/// Deletes an account and what is theirs, leaving other people's events working.
///
/// <list type="bullet">
/// <item><b>Their events with another celebrant</b> carry on with everything in them and pass to one of
/// those people, chosen at random, preferring someone with an account. A chosen celebrant with no account
/// has full access through their celebrant row the moment they sign in with that contact, and
/// <see cref="EventInheritance"/> makes them owner then. The event's old keys are replaced either way.</item>
/// <item><b>Their events with nobody else</b> are deleted, with guest lists, replies, invitations, albums
/// and photos, whoever took them.</item>
/// <item><b>An event someone else's account started that names them as host:</b> stays. The account
/// behind it typed that contact in, and one person deleting their account can't take it from them.</item>
/// <item><b>An event they started for a venue they work at, with no other celebrant:</b> passes to the
/// venue's owner. Albums they made on someone else's event pass to whoever runs that event.</item>
/// <item><b>Other people's events:</b> what they added stays, with the name shown on it: photos are event
/// content and hold no link to an account. Their comments, likes and celebrant rows go. Guest rows stay:
/// those are the host's list, matched to accounts by contact only.</item>
/// <item><b>Payments:</b> kept for the accounts with no link to the person; the event's name comes out
/// of the description. Automatic renewal is stopped first, in its own commit.</item>
/// <item><b>Templates:</b> anything still used (or made for someone else) keeps working, no longer
/// credited, and leaves the gallery. Anything else of theirs is deleted.</item>
/// </list>
///
/// <para>One transaction: a failure leaves the account whole, so they can sign in and try again. Files
/// and the bank's customer record are outside it, so they go into <see cref="PendingRemoval"/> in the
/// same transaction and are removed by <see cref="PendingRemovalService"/> after the commit.</para>
///
/// <para>Why not <c>CampaignService.DeleteAsync</c>: it answers to the caller's ownership and
/// saves as it goes, and it leaves albums and photos behind, so it can't be one step of this.</para>
/// </summary>
public sealed class AccountDeletionService(
    AppDbContext db,
    ICurrentUser currentUser,
    IOtpService otp,
    IStorageService storage,
    PendingRemovalSignal removals,
    IEmailSender email,
    IConfiguration config,
    Application.Services.Billing.IGooglePlayPublisher play,
    ILogger<AccountDeletionService> logger) : IAccountDeletionService
{
    private const string LastAdmin =
        "You're the only admin. Make someone else an admin first, then you can delete this account.";

    /// <summary>Serialises deletions of admins, so two can't each see the other as the remaining one.</summary>
    private const long LastAdminLock = 0x1B_DE1E7E_AD;

    public async Task<AccountDeletionSummaryDto> SummaryAsync(CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var scope = await ScopeAsync(me, ct);
        var now = DateTimeOffset.UtcNow;

        var eventIds = scope.DeletedEvents;
        var bucketIds = await db.MediaBuckets.Where(b => eventIds.Contains(b.CampaignId)).Select(b => b.Id).ToListAsync(ct);
        var media = await db.EventPhotos
            .Where(p => p.DeletedAt == null
                && ((p.CampaignId != null && eventIds.Contains(p.CampaignId.Value))
                    || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value))))
            .Select(p => p.ContentType)
            .ToListAsync(ct);
        var videos = media.Count(t => t.StartsWith("video/", StringComparison.OrdinalIgnoreCase));

        var (channel, sentTo) = Destination(me);
        var storeRenews = await RenewingPlaySubscriptions(me.Id, now).AnyAsync(ct);
        return new AccountDeletionSummaryDto(
            eventIds.Count, scope.Passed.Count, bucketIds.Count, media.Count - videos, videos,
            await PaidAsync(me, eventIds, now, ct),
            me.AutoRenewKind is not null || storeRenews,
            await IsLastAdminAsync(me.Id, ct) ? LastAdmin : null,
            channel, sentTo,
            // Only say we'll stop it if we can. If we can't, they have to, or Google keeps charging.
            !storeRenews ? null : play.IsConfigured
                ? "Your Premium pass stops renewing on Google Play."
                : "Your Premium pass renews through Google Play. Cancel it there too, or Google keeps charging you.");
    }

    public async Task<CodeSentResponse> RequestCodeAsync(CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        if (await IsLastAdminAsync(me.Id, ct)) throw new BusinessRuleException(LastAdmin, "last_admin");

        var (channel, sentTo) = Destination(me);
        var request = channel == "email"
            ? new SendOtpRequest("email", null, me.Email, null)
            : new SendOtpRequest("sms", me.PhoneE164, null, null);
        var challenge = await otp.RequestAsync(request, OtpPurpose.AccountDeletion, ct);
        return new CodeSentResponse(challenge.ChallengeId, channel, sentTo, challenge.ExpiresInSeconds);
    }

    public async Task<AccountDeletedDto> DeleteAsync(VerifyCodeRequest request, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        // Before the code is spent: refused means nothing changed, the code included.
        if (await IsLastAdminAsync(me.Id, ct)) throw new BusinessRuleException(LastAdmin, "last_admin");

        var verified = await otp.VerifyContactForAsync(new VerifyOtpRequest(request.ChallengeId, request.Code), OtpPurpose.AccountDeletion, ct);
        var theirs = verified.ContactType == "phone" ? me.PhoneE164 : me.Email;
        if (!string.Equals(theirs, verified.Contact, StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("That code isn't for this account.", "code_not_for_account");

        // First and on its own, so no card is charged again whatever happens below.
        await db.Users.Where(u => u.Id == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.AutoRenewKind, (PaymentKind?)null), ct);

        // Not ct: once the code is spent and the work started, a closed page shouldn't abandon it.
        await using var tx = await db.Database.BeginTransactionAsync(CancellationToken.None);
        var counts = await DeleteEverythingAsync(me.Id, CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);

        // Ids and counts only: this line outlives the person.
        logger.LogInformation("An account was deleted: {Events} events deleted, {Passed} passed on, {Files} removals queued.", counts.Events, counts.Told.Count, counts.Removals);
        removals.Wake();
        await TellNewOwnersAsync(counts.Told);
        return new AccountDeletedDto(true);
    }

    // ----- What it reaches ---------------------------------------------------------------------

    /// <summary>Who an event goes to. No account means it waits for that celebrant to sign in.</summary>
    private sealed record Successor(Guid? UserId, Guid? CelebrantId, string? Email);

    /// <summary>Everything about the account that decides what happens to whom. Reads only.</summary>
    private sealed record Scope(
        List<Guid> DeletedEvents,
        Dictionary<Guid, Successor> Passed,
        List<Guid> MyInviters,
        List<string> Emails,
        List<string> Phones);

    private async Task<Scope> ScopeAsync(AppUser me, CancellationToken ct)
    {
        var email = me.Email?.Trim().ToLowerInvariant();
        var phone = me.PhoneE164?.Trim();

        var myInviters = await db.Inviters
            .Where(i => (email != null && i.Email == email) || (phone != null && i.PhoneE164 == phone))
            .Select(i => i.Id)
            .ToListAsync(ct);

        // Contacts this person proved through a link count as theirs too, as they do for their inbox.
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var phones = new HashSet<string>();
        if (!string.IsNullOrEmpty(email)) emails.Add(email);
        if (!string.IsNullOrEmpty(phone)) phones.Add(phone);
        var links = await db.VerifiedContactLinks
            .Where(l => (email != null && l.Email == email) || (phone != null && l.PhoneE164 == phone))
            .ToListAsync(ct);
        foreach (var link in links) { emails.Add(link.Email); phones.Add(link.PhoneE164); }
        var emailList = emails.ToList();
        var phoneList = phones.ToList();

        // Buckets whose oldest owner is this account decide the owner of an event nobody is recorded on.
        var ownBuckets = await db.MediaBuckets.Where(b => b.OwnerUserId == me.Id).Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        var candidates = await db.Campaigns
            .Where(c => c.CreatedByUserId == me.Id
                        || (c.InviterId != null && myInviters.Contains(c.InviterId.Value))
                        || (c.CreatedByUserId == null && c.InviterId == null && ownBuckets.Contains(c.Id)))
            .Select(c => new { c.Id, c.CreatedByUserId, c.VenueId })
            .ToListAsync(ct);

        var creators = candidates.Select(c => c.CreatedByUserId).OfType<Guid>().Distinct().ToList();
        var livingCreators = (await db.Users.Where(u => creators.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct)).ToHashSet();
        var venueIds = candidates.Select(c => c.VenueId).OfType<Guid>().Distinct().ToList();
        var venueOwners = await db.Venues.Where(v => venueIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.OwnerUserId, ct);

        var eventIds = candidates.Select(c => c.Id).ToList();
        var others = (await db.CampaignCelebrants.Where(c => eventIds.Contains(c.CampaignId)).ToListAsync(ct))
            .Where(c => !(c.Email is not null && emails.Contains(c.Email)) && !(c.PhoneE164 is not null && phones.Contains(c.PhoneE164)))
            .ToList();
        var theirEmails = others.Select(c => c.Email).OfType<string>().Distinct().ToList();
        var theirPhones = others.Select(c => c.PhoneE164).OfType<string>().Distinct().ToList();
        var accounts = await db.Users
            .Where(u => u.Id != me.Id && u.IsActive
                        && ((u.Email != null && theirEmails.Contains(u.Email)) || (u.PhoneE164 != null && theirPhones.Contains(u.PhoneE164))))
            .Select(u => new { u.Id, u.Email, u.PhoneE164 })
            .ToListAsync(ct);

        var deleted = new List<Guid>();
        var passed = new Dictionary<Guid, Successor>();
        foreach (var c in candidates)
        {
            if (c.CreatedByUserId is { } creator && creator != me.Id && livingCreators.Contains(creator))
                continue;   // someone else's account started it

            var people = others.Where(o => o.CampaignId == c.Id)
                .Select(o => (Row: o, Account: accounts.FirstOrDefault(a =>
                    (o.Email != null && string.Equals(a.Email, o.Email, StringComparison.OrdinalIgnoreCase))
                    || (o.PhoneE164 != null && a.PhoneE164 == o.PhoneE164))))
                .ToList();
            if (people.Count > 0)
            {
                var withAccount = people.Where(p => p.Account is not null).ToList();
                var from = withAccount.Count > 0 ? withAccount : people;
                var chosen = from[Random.Shared.Next(from.Count)];
                passed[c.Id] = new Successor(chosen.Account?.Id, chosen.Row.Id, chosen.Account?.Email ?? chosen.Row.Email);
            }
            else if (c.CreatedByUserId == me.Id && c.VenueId is { } venue
                     && venueOwners.TryGetValue(venue, out var owner) && owner != me.Id)
                passed[c.Id] = new Successor(owner, null, null);   // started for a venue they work at
            else
                deleted.Add(c.Id);
        }

        return new Scope(deleted, passed, myInviters, emailList, phoneList);
    }

    /// <summary>Store subscriptions that will charge again unless someone stops them.</summary>
    private IQueryable<GooglePlayPurchase> RenewingPlaySubscriptions(Guid userId, DateTimeOffset now) =>
        db.GooglePlayPurchases.Where(p => p.UserId == userId && p.CampaignId == null && p.AutoRenewing && p.VoidedAt == null && p.ExpiresAt > now);

    private async Task<List<string>> PaidAsync(AppUser me, List<Guid> eventIds, DateTimeOffset now, CancellationToken ct)
    {
        var paid = new List<string>();
        if (PlanRules.IsActive(me.SubscriptionTier, me.SubscriptionEndsAt, now))
        {
            if (me.SubscriptionTier == SubscriptionTier.Venue) paid.Add("Venue plan");
            else if (me.SubscriptionTier == SubscriptionTier.Premium) paid.Add("Premium pass");
        }

        var running = await db.Campaigns
            .Where(c => eventIds.Contains(c.Id)
                        && ((c.EventPass != EventPassKind.None && c.EventPassUntil > now) || c.KeepPhotosUntil > now))
            .Select(c => new { c.Title, c.EventPass, c.EventPassUntil, c.KeepPhotosUntil })
            .ToListAsync(ct);
        foreach (var c in running)
        {
            if (c.EventPass != EventPassKind.None && c.EventPassUntil > now)
                paid.Add($"{(c.EventPass == EventPassKind.Platinum ? "Platinum" : "Gold")} pass on {c.Title}");
            if (c.KeepPhotosUntil > now) paid.Add($"Keep your photos on {c.Title}");
        }

        var credits = await db.PassCredits.CountAsync(p => p.OwnerUserId == me.Id && p.UsedOnCampaignId == null, ct);
        if (credits > 0) paid.Add(credits == 1 ? "1 unused pass" : $"{credits} unused passes");
        return paid;
    }

    private async Task<bool> IsLastAdminAsync(Guid userId, CancellationToken ct)
    {
        var admin = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (admin is not { } roleId) return false;
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId, ct)) return false;
        return await db.UserRoles.CountAsync(ur => ur.RoleId == roleId, ct) <= 1;
    }

    private async Task<AppUser> MeAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var me = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new UnauthorizedException();
        if (!me.IsActive) throw new AccountSuspendedException();
        return me;
    }

    private static (string Channel, string SentTo) Destination(AppUser me) =>
        !string.IsNullOrWhiteSpace(me.Email) ? ("email", AccountService.MaskEmail(me.Email))
        : !string.IsNullOrWhiteSpace(me.PhoneE164) ? ("sms", AccountService.MaskPhone(me.PhoneE164))
        : throw new BusinessRuleException("This account has no email or phone to send a code to. Contact us to delete it.", "no_contact");

    // ----- Doing it ----------------------------------------------------------------------------

    /// <summary>Somebody to tell that an event is now theirs to run.</summary>
    private sealed record NewOwner(string? Email, Guid CampaignId, string Title, bool HasAccount);

    private async Task<(int Events, int Removals, List<NewOwner> Told)> DeleteEverythingAsync(Guid userId, CancellationToken ct)
    {
        // Held to the end of the transaction. A concurrent attempt waits here, then finds no account.
        var locked = await db.Users.FromSqlInterpolated($"SELECT * FROM users WHERE id = {userId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        if (locked.Count == 0) throw new UnauthorizedException();
        var me = locked[0];

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LastAdminLock})", ct);
        if (await IsLastAdminAsync(me.Id, ct)) throw new BusinessRuleException(LastAdmin, "last_admin");

        var now = DateTimeOffset.UtcNow;
        var scope = await ScopeAsync(me, ct);   // again, inside the lock: it may have changed since the summary
        var queue = new RemovalQueue(storage);

        await DeleteEventsAsync(scope.DeletedEvents, queue, now, ct);
        var told = await PassEventsOnAsync(me, scope, now, ct);
        await PassOtherAlbumsOnAsync(me.Id, now, ct);
        await RemoveFeedActivityAsync(me.Id, ct);

        await db.CampaignCelebrants
            .Where(c => (c.Email != null && scope.Emails.Contains(c.Email)) || (c.PhoneE164 != null && scope.Phones.Contains(c.PhoneE164)))
            .ExecuteDeleteAsync(ct);
        await db.VenueStaff.Where(s => scope.Emails.Contains(s.Email)).ExecuteDeleteAsync(ct);
        await RemoveVenueAsync(me.Id, queue, now, ct);
        await db.PassCredits.Where(p => p.OwnerUserId == me.Id).ExecuteDeleteAsync(ct);
        await HandleTemplatesAsync(me, scope, queue, now, ct);
        await KeepPaymentsWithoutThePersonAsync(me, scope.DeletedEvents, queue, ct);

        await db.Inquiries.Where(i => scope.Emails.Contains(i.Email)).ExecuteDeleteAsync(ct);
        await db.OtpChallenges
            .Where(o => (o.Email != null && scope.Emails.Contains(o.Email)) || (o.PhoneE164 != null && scope.Phones.Contains(o.PhoneE164)))
            .ExecuteDeleteAsync(ct);
        await db.VerifiedContactLinks
            .Where(l => scope.Emails.Contains(l.Email) || scope.Phones.Contains(l.PhoneE164))
            .ExecuteDeleteAsync(ct);
        // A host row still on someone else's event is their data, like a guest row.
        await db.Inviters
            .Where(i => scope.MyInviters.Contains(i.Id) && !db.Campaigns.Any(c => c.InviterId == i.Id))
            .ExecuteDeleteAsync(ct);

        queue.Prefix($"avatars/{me.Id:N}/");
        queue.Url(me.AvatarUrl);

        db.PendingRemovals.AddRange(queue.Rows(now));
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "account.delete",
            Actor = "self",
            DataJson = JsonSerializer.Serialize(new { events = scope.DeletedEvents.Count }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);

        // Roles and external logins go by cascade. Everything else pointing here was dealt with above,
        // and anything missed fails this statement and rolls the whole thing back.
        await db.Users.Where(u => u.Id == me.Id).ExecuteDeleteAsync(ct);
        return (scope.DeletedEvents.Count, queue.Count, told);
    }

    private async Task DeleteEventsAsync(List<Guid> ids, RemovalQueue queue, DateTimeOffset now, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var bucketIds = await db.MediaBuckets.Where(b => ids.Contains(b.CampaignId)).Select(b => b.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            queue.Prefix($"campaigns/{id:N}/");
            queue.Prefix($"imported/{id:N}/");
        }
        foreach (var id in bucketIds) queue.Prefix($"buckets/{id:N}/");

        // Deleted rows included: a row already marked removed may have had its file removal fail. The
        // prefixes above cover nearly all of them; anything older or stored elsewhere is listed singly.
        var photoUrls = await db.EventPhotos
            .Where(p => (p.CampaignId != null && ids.Contains(p.CampaignId.Value)) || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value)))
            .Select(p => new { p.OriginalUrl, p.Url, p.ThumbUrl })
            .ToListAsync(ct);
        foreach (var p in photoUrls) { queue.Url(p.OriginalUrl); queue.Url(p.Url); queue.Url(p.ThumbUrl); }
        foreach (var url in await db.CampaignAssets.Where(a => ids.Contains(a.CampaignId)).Select(a => a.Url).ToListAsync(ct)) queue.Url(url);

        foreach (var key in await db.PendingUploads
                     .Where(u => bucketIds.Contains(u.BucketId) || (u.CampaignId != null && ids.Contains(u.CampaignId.Value)))
                     .Select(u => u.IncomingKey).ToListAsync(ct))
            queue.Incoming(key);
        await db.PendingUploads
            .Where(u => bucketIds.Contains(u.BucketId) || (u.CampaignId != null && ids.Contains(u.CampaignId.Value)))
            .ExecuteDeleteAsync(ct);

        var inviteIds = db.Invites.Where(i => ids.Contains(i.CampaignId)).Select(i => i.Id);
        await db.DeliveryAttempts.Where(a => inviteIds.Contains(a.InviteId)).ExecuteDeleteAsync(ct);
        await db.RsvpResponses.Where(r => inviteIds.Contains(r.InviteId)).ExecuteDeleteAsync(ct);
        await db.InviteTrustedIps.Where(t => inviteIds.Contains(t.InviteId)).ExecuteDeleteAsync(ct);
        await db.Invites.Where(i => ids.Contains(i.CampaignId)).ExecuteDeleteAsync(ct);

        // Rows, not just marks: nothing is left to point at them, and they hold names.
        await db.EventPhotos
            .Where(p => (p.CampaignId != null && ids.Contains(p.CampaignId.Value)) || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value)))
            .ExecuteDeleteAsync(ct);
        await db.MediaBucketQrs.Where(q => bucketIds.Contains(q.BucketId)).ExecuteDeleteAsync(ct);
        await db.MediaBuckets.Where(b => ids.Contains(b.CampaignId)).ExecuteDeleteAsync(ct);   // members cascade
        await db.Guests.Where(g => ids.Contains(g.CampaignId)).ExecuteDeleteAsync(ct);
        await db.UploadedGuestFiles.Where(f => ids.Contains(f.CampaignId)).ExecuteDeleteAsync(ct);
        await db.CampaignAssets.Where(a => ids.Contains(a.CampaignId)).ExecuteDeleteAsync(ct);

        var templateIds = await db.Campaigns.Where(c => ids.Contains(c.Id)).Select(c => c.TemplateId).Distinct().ToListAsync(ct);

        // A save the date points at the invitation made from it; that invitation may not be going.
        await db.Campaigns.Where(c => c.InvitationCampaignId != null && ids.Contains(c.InvitationCampaignId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.InvitationCampaignId, (Guid?)null), ct);
        // Comments, likes and celebrants cascade; a pass credit used on it comes free of it.
        await db.Campaigns.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);

        // An imported or album-only event has a template row of its own, which goes with it.
        var own = await db.Templates
            .Where(t => templateIds.Contains(t.Id) && t.Visibility == TemplateVisibility.Imported && !db.Campaigns.Any(c => c.TemplateId == t.Id))
            .Select(t => t.Id)
            .ToListAsync(ct);
        await db.TemplateAssets.Where(a => own.Contains(a.TemplateId)).ExecuteDeleteAsync(ct);
        await db.TemplateReports.Where(r => own.Contains(r.TemplateId)).ExecuteDeleteAsync(ct);
        await db.Templates.Where(t => own.Contains(t.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Hands each event over with everything in it. The old organiser's possession and dashboard links
    /// are replaced, so what's left in their browser or inbox opens nothing, and their host contact comes
    /// off the invitation. With no account yet the event has no owner until the chosen celebrant signs in:
    /// nobody can take it, since every route to host rights is an account, a contact it has proved or a
    /// key, and the celebrant row only ever matches a proved contact.
    /// </summary>
    private async Task<List<NewOwner>> PassEventsOnAsync(AppUser me, Scope scope, DateTimeOffset now, CancellationToken ct)
    {
        var told = new List<NewOwner>();
        // Their Premium was lifting these events' cover. Photos' lapse counts from today, as when a plan
        // ends, rather than an old event suddenly being past its cover and removed with no notice.
        var premium = me.SubscriptionTier == SubscriptionTier.Premium && PlanRules.IsActive(me.SubscriptionTier, me.SubscriptionEndsAt, now);

        foreach (var (id, next) in scope.Passed)
        {
            var newKey = TokenService.Hash(TokenService.GenerateToken());
            await db.Campaigns.Where(c => c.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CreatedByUserId, next.UserId)
                .SetProperty(c => c.AccessTokenHash, newKey)
                .SetProperty(c => c.DashboardTokenHash, (string?)null)
                .SetProperty(c => c.InviterId, c => c.InviterId != null && scope.MyInviters.Contains(c.InviterId.Value) ? null : c.InviterId)
                .SetProperty(c => c.KeepPhotosUntil, c => premium && (c.KeepPhotosUntil == null || c.KeepPhotosUntil < now) ? now : c.KeepPhotosUntil)
                .SetProperty(c => c.UpdatedAt, now), ct);
            await db.MediaBuckets.Where(b => b.CampaignId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.OwnerUserId, next.UserId ?? Guid.Empty).SetProperty(b => b.UpdatedAt, now), ct);

            if (next.CelebrantId is { } celebrant)
            {
                await db.CampaignCelebrants.Where(c => c.Id == celebrant).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.CanManage, true)
                    .SetProperty(c => c.InheritsOwnership, next.UserId == null), ct);
                var title = await db.Campaigns.Where(c => c.Id == id).Select(c => c.Title).FirstAsync(ct);
                told.Add(new NewOwner(next.Email, id, title, next.UserId is not null));
            }
        }
        return told;
    }

    /// <summary>
    /// A short note to each new owner. After the commit and best effort: the event is theirs whether or
    /// not this arrives, and it shows in their list when they sign in. A celebrant with only a phone has
    /// no email to send it to.
    /// </summary>
    private async Task TellNewOwnersAsync(List<NewOwner> told)
    {
        foreach (var t in told.Where(t => !string.IsNullOrWhiteSpace(t.Email)))
        {
            var title = WebUtility.HtmlEncode(t.Title);
            var link = $"{config.InviterBase()}/dashboard/{t.CampaignId}";
            var how = t.HasAccount ? "It's in your events when you sign in." : "Sign up with this email address to open it.";
            try
            {
                await email.SendAsync(new EmailMessage(
                    To: t.Email!,
                    Subject: $"You now manage {t.Title}",
                    Html: $"<p>The person who organised <strong>{title}</strong> has deleted their invites.blog account, so you now manage the event. Its guests, replies and photos are all still there.</p><p>{how}</p><p><a href=\"{link}\">Open the event</a></p>",
                    Stream: EmailStream.System,
                    Text: $"The person who organised {t.Title} has deleted their invites.blog account, so you now manage the event. Its guests, replies and photos are all still there.\n\n{how}\n\n{link}",
                    Tags: new[] { new KeyValuePair<string, string>("kind", "event_passed_on") }), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Couldn't tell a new owner about event {CampaignId}.", t.CampaignId);
            }
        }
    }

    /// <summary>
    /// An album they made on an event that stays (they ran it as a celebrant, say) has guests' photos in
    /// it, so it passes to whoever runs the event. With nobody recorded it holds the same empty owner the
    /// album service gives such an album.
    /// </summary>
    private async Task PassOtherAlbumsOnAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var left = await db.MediaBuckets.Where(b => b.OwnerUserId == userId)
            .Select(b => new { b.Id, b.CampaignId })
            .ToListAsync(ct);
        foreach (var album in left)
        {
            var creator = await db.Campaigns.Where(c => c.Id == album.CampaignId).Select(c => c.CreatedByUserId).FirstOrDefaultAsync(ct);
            var owner = creator is { } c0 && c0 != userId ? c0 : Guid.Empty;
            await db.MediaBuckets.Where(b => b.Id == album.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.OwnerUserId, owner).SetProperty(b => b.UpdatedAt, now), ct);
        }
    }

    private async Task RemoveFeedActivityAsync(Guid userId, CancellationToken ct)
    {
        await db.CommentLikes.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PostLikes.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);

        // The feed shows a reply only under its comment. Other people's replies to theirs are other
        // people's words, so they move up to the top level instead of disappearing.
        var theirs = db.PostComments.Where(c => c.UserId == userId).Select(c => c.Id);
        await db.PostComments.Where(c => c.ParentId != null && theirs.Contains(c.ParentId.Value) && c.UserId != userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, (Guid?)null), ct);
        await db.PostComments.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
    }

    private async Task RemoveVenueAsync(Guid userId, RemovalQueue queue, DateTimeOffset now, CancellationToken ct)
    {
        var venues = await db.Venues.Where(v => v.OwnerUserId == userId).Select(v => v.Id).ToListAsync(ct);
        if (venues.Count == 0) return;

        foreach (var id in venues) queue.Prefix($"venues/{id:N}/");

        // Events other people hold there stay and stop being the venue's (the foreign key clears them).
        // Their photos' lapse counts from today, as when a venue's plan ends, so a months-old event isn't
        // suddenly past its cover and removed on the next sweep with no notice.
        await db.Campaigns
            .Where(c => c.VenueId != null && venues.Contains(c.VenueId.Value) && (c.KeepPhotosUntil == null || c.KeepPhotosUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.KeepPhotosUntil, now), ct);
        await db.Venues.Where(v => venues.Contains(v.Id)).ExecuteDeleteAsync(ct);   // staff cascade
    }

    /// <summary>
    /// An event pins its template's package, so anything in use must go on being served. Nobody can
    /// manage a template once its author is gone, so one in use is taken out of the gallery, the way
    /// its author deleting it would (<c>MyTemplatesService.DeleteAsync</c>); one made for somebody else
    /// is promised to them and stays usable by them. Their designs are the working copies: published
    /// templates hold their own scene and package, so all of those go.
    /// </summary>
    private async Task HandleTemplatesAsync(AppUser me, Scope scope, RemovalQueue queue, DateTimeOffset now, CancellationToken ct)
    {
        await db.TemplateDesigns.Where(d => d.OwnerUserId == me.Id).ExecuteDeleteAsync(ct);   // publishes cascade

        var authored = await db.Templates.Where(t => t.DesignerUserId == me.Id)
            .Select(t => new { t.Id, t.Slug, t.AssignedEmail })
            .ToListAsync(ct);
        foreach (var t in authored)
        {
            var inUse = await db.Campaigns.AnyAsync(c => c.TemplateId == t.Id, ct)
                        || await db.TemplateDesigns.AnyAsync(d => d.TemplateId == t.Id, ct);
            var forSomeoneElse = t.AssignedEmail is not null && !scope.Emails.Contains(t.AssignedEmail);
            if (inUse || forSomeoneElse)
            {
                await db.Templates.Where(x => x.Id == t.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.DesignerUserId, (Guid?)null)
                    .SetProperty(x => x.DesignerName, (string?)null)
                    .SetProperty(x => x.DesignerInviterId, (Guid?)null)
                    .SetProperty(x => x.AssignedEmail, x => scope.Emails.Contains(x.AssignedEmail!) ? null : x.AssignedEmail)
                    .SetProperty(x => x.IsActive, forSomeoneElse)
                    .SetProperty(x => x.UpdatedAt, now), ct);
                continue;
            }

            queue.Prefix($"templates/{t.Slug}@");
            await db.TemplateAssets.Where(a => a.TemplateId == t.Id).ExecuteDeleteAsync(ct);
            await db.TemplateReports.Where(r => r.TemplateId == t.Id).ExecuteDeleteAsync(ct);
            await db.Templates.Where(x => x.Id == t.Id).ExecuteDeleteAsync(ct);
        }

        // Someone else's template made for this person, or crediting this host: the row stays, the
        // contact doesn't.
        await db.Templates.Where(t => t.AssignedEmail != null && scope.Emails.Contains(t.AssignedEmail))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssignedEmail, (string?)null), ct);
        await db.Templates.Where(t => t.DesignerInviterId != null && scope.MyInviters.Contains(t.DesignerInviterId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DesignerInviterId, (Guid?)null), ct);

        await db.TemplateReports.Where(r => r.ReporterUserId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReporterUserId, (Guid?)null), ct);
        await db.TemplateReports.Where(r => r.ResolvedByUserId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ResolvedByUserId, (Guid?)null), ct);
    }

    /// <summary>
    /// What the accounts need stays: amount, currency, kind, dates, status, the bank's references, the
    /// terms accepted. What goes is who it was, and the event's name in the description.
    /// </summary>
    private async Task KeepPaymentsWithoutThePersonAsync(AppUser me, List<Guid> deletedEvents, RemovalQueue queue, CancellationToken ct)
    {
        var rows = await db.Payments
            .Where(p => p.UserId == me.Id || (p.CampaignId != null && deletedEvents.Contains(p.CampaignId.Value)))
            .ToListAsync(ct);
        foreach (var p in rows)
        {
            if (p.UserId == me.Id) p.UserId = null;
            if (p.CampaignId is { } c && deletedEvents.Contains(c) && p.Description is not null)
                p.Description = BillingService.Describe(p.Kind, p.Quantity, null);
        }

        if (!string.IsNullOrWhiteSpace(me.PaymentCustomerId)) queue.PaymentCustomer(me.PaymentCustomerId);

        // Store purchases: same treatment. The token stays so it can never be redeemed again, with no
        // link to the person. A subscription still renewing is stopped at Google after the commit.
        foreach (var token in await RenewingPlaySubscriptions(me.Id, DateTimeOffset.UtcNow).Select(p => p.PurchaseToken).ToListAsync(ct))
            queue.PlaySubscription(token);
        await db.GooglePlayPurchases.Where(p => p.UserId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, (Guid?)null).SetProperty(p => p.AutoRenewing, false), ct);
        await db.GooglePlayPurchases.Where(p => p.CampaignId != null && deletedEvents.Contains(p.CampaignId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserId, (Guid?)null).SetProperty(p => p.CampaignId, (Guid?)null), ct);
    }

    /// <summary>Collects what to remove after the commit, each thing once.</summary>
    private sealed class RemovalQueue(IStorageService storage)
    {
        private readonly HashSet<(string Kind, string Target)> _items = [];
        private readonly HashSet<string> _prefixes = [];

        public int Count => _items.Count;

        public void Prefix(string prefix)
        {
            if (!StorageKeyPrefix.IsSafe(prefix)) throw new InvalidOperationException("Refusing an unsafe removal prefix.");
            _prefixes.Add(prefix);
            _items.Add((PendingRemovalKinds.Prefix, prefix));
        }

        public void Url(string? url)
        {
            if (string.IsNullOrWhiteSpace(url) || storage.KeyFor(url) is not { Length: > 0 } key) return;
            if (!_prefixes.Any(key.StartsWith)) _items.Add((PendingRemovalKinds.File, key));
        }

        public void Incoming(string key)
        {
            if (!string.IsNullOrWhiteSpace(key)) _items.Add((PendingRemovalKinds.IncomingFile, key));
        }

        public void PaymentCustomer(string id) => _items.Add((PendingRemovalKinds.PaymentCustomer, id));

        public void PlaySubscription(string token) => _items.Add((PendingRemovalKinds.PlaySubscription, token));

        public IEnumerable<PendingRemoval> Rows(DateTimeOffset now) => _items.Select(i => new PendingRemoval
        {
            Id = Guid.NewGuid(),
            Kind = i.Kind,
            Target = i.Target,
            NextAttemptAt = now,
            CreatedAt = now,
        });
    }
}
