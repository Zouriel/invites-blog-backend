using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Contacts;

/// <inheritdoc cref="IContactLinkService"/>
public sealed class ContactLinkService(
    ICurrentUser currentUser,
    IGuestRepository guests,
    IRepository<Invite> invites,
    IRepository<VerifiedContactLink> links,
    IOtpService otp,
    IUnitOfWork uow) : IContactLinkService
{
    public async Task<IReadOnlyList<LinkableContact>> GetLinkableAsync(CancellationToken ct = default)
    {
        var (type, contact) = Caller();
        var candidates = await DiscoverAsync(type, contact, ct);
        if (candidates.Count == 0) return [];

        var otherType = type == "phone" ? "email" : "phone";
        var result = new List<LinkableContact>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var count = await CountInvitesAsync(otherType, candidate, ct);
            if (count == 0) continue;   // nothing to gain — don't ask them to verify for an empty inbox
            result.Add(new LinkableContact(otherType, Mask(otherType, candidate), count));
        }
        return result;
    }

    public async Task<Guid> RequestLinkCodeAsync(string maskedContact, CancellationToken ct = default)
    {
        var (type, contact) = Caller();
        var target = await ResolveOfferedAsync(type, contact, maskedContact, ct);
        var otherType = type == "phone" ? "email" : "phone";

        // Reuses the normal OTP path so send limits, expiry and channel availability stay in one place.
        var req = otherType == "email"
            ? new SendOtpRequest("email", null, target, null)
            : new SendOtpRequest("sms", target, null, null);
        var challenge = await otp.RequestAsync(req, ct: ct);
        return challenge.ChallengeId;
    }

    public async Task<ContactLinkResult> VerifyLinkAsync(Guid challengeId, string code, CancellationToken ct = default)
    {
        var (type, contact) = Caller();

        // Consume the challenge FIRST so a wrong code burns an attempt exactly like any other OTP,
        // then re-check the pairing is still one we offered — a challenge for some other contact
        // must not be redeemable here.
        var verified = await otp.VerifyContactAsync(new VerifyOtpRequest(challengeId, code), ct);
        if (verified.ContactType == type)
            throw new BusinessRuleException(
                "That's the contact you already signed in with.", "contact_already_verified");

        var offered = await DiscoverAsync(type, contact, ct);
        if (!offered.Contains(verified.Contact, StringComparer.OrdinalIgnoreCase))
            throw new BusinessRuleException(
                "That contact isn't on any invitation we can match to your account.", "contact_not_linkable");

        var email = type == "email" ? contact : verified.Contact;
        var phone = type == "phone" ? contact : verified.Contact;

        var existing = await links.FirstOrDefaultAsync(l => l.Email == email && l.PhoneE164 == phone, ct);
        if (existing is not null)
            return new ContactLinkResult(false, verified.ContactType, Mask(verified.ContactType, verified.Contact));

        await links.AddAsync(new VerifiedContactLink
        {
            Id = Guid.NewGuid(),
            Email = email,
            PhoneE164 = phone,
            VerifiedFrom = type,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
        await uow.SaveChangesAsync(ct);

        return new ContactLinkResult(true, verified.ContactType, Mask(verified.ContactType, verified.Contact));
    }

    // ---- discovery -------------------------------------------------------------------------

    /// <summary>
    /// The other-kind contacts that share a guest row with <paramref name="contact"/> — the hosts'
    /// own assertion that these belong to one person. Unverified by definition, so this only ever
    /// feeds an offer. Anything already linked is dropped.
    /// </summary>
    private async Task<IReadOnlyList<string>> DiscoverAsync(string type, string contact, CancellationToken ct)
    {
        var rows = type == "phone"
            ? await guests.Query().Where(g => g.PhoneE164 == contact && g.Email != null)
                .Select(g => g.Email!).Distinct().ToListAsync(ct)
            : await guests.Query().Where(g => g.Email == contact && g.PhoneE164 != null)
                .Select(g => g.PhoneE164!).Distinct().ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Link rows for one person are a handful at most, so filter them in memory and keep the
        // repository abstraction (Query() is EF-only and can't be exercised in unit tests).
        var linked = type == "phone"
            ? (await links.ListAsync(l => l.PhoneE164 == contact, ct)).Select(l => l.Email)
            : (await links.ListAsync(l => l.Email == contact, ct)).Select(l => l.PhoneE164);

        var already = linked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return rows.Where(r => !already.Contains(r)).ToList();
    }

    private async Task<string> ResolveOfferedAsync(
        string type, string contact, string masked, CancellationToken ct)
    {
        var otherType = type == "phone" ? "email" : "phone";
        var candidates = await DiscoverAsync(type, contact, ct);
        var match = candidates.FirstOrDefault(c =>
            string.Equals(Mask(otherType, c), masked, StringComparison.Ordinal));
        return match ?? throw new BusinessRuleException(
            "That contact isn't available to link.", "contact_not_linkable");
    }

    private async Task<int> CountInvitesAsync(string type, string contact, CancellationToken ct)
    {
        var guestIds = type == "email"
            ? await guests.Query().Where(g => g.Email == contact).Select(g => g.Id).ToListAsync(ct)
            : await guests.Query().Where(g => g.PhoneE164 == contact).Select(g => g.Id).ToListAsync(ct);
        if (guestIds.Count == 0) return 0;
        return await invites.Query().Where(i => guestIds.Contains(i.GuestId)).CountAsync(ct);
    }

    private (string Type, string Contact) Caller()
    {
        var type = currentUser.ContactType;
        var contact = currentUser.Contact;
        if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(contact))
            throw new UnauthorizedException();
        return (type, contact);
    }

    // ---- masking ---------------------------------------------------------------------------

    internal static string Mask(string type, string value) =>
        type == "phone" ? MaskPhone(value) : MaskEmail(value);

    /// <summary>a•••a@example.com — enough to recognise, not enough to harvest.</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        var name = email[..at];
        var domain = email[at..];
        if (name.Length <= 2) return $"{name[0]}•••{domain}";
        return $"{name[0]}•••{name[^1]}{domain}";
    }

    /// <summary>+960•••9157 — keeps the country code and last four.</summary>
    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 5) return phone;
        var head = phone.Length >= 8 ? phone[..4] : phone[..2];
        return $"{head}•••{phone[^4..]}";
    }
}
