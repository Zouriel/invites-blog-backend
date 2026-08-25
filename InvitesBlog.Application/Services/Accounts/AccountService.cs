using System.Security.Claims;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Accounts;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Accounts;

/// <summary>
/// One sign-in for everyone; roles decide what they see afterwards.
/// <para>
/// An account is reachable by email, by phone, or by both. Linking the second identifier is what
/// merges a designer's email account with the phone they used as a customer — and because a
/// campaign belongs to an <see cref="Inviter"/> keyed on email/phone rather than to an account, the
/// merge is mostly about widening the LOOKUP, not re-parenting rows.
/// </para>
/// </summary>
public sealed class AccountService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IRepository<UserExternalLogin> externalLogins,
    IRepository<Inviter> inviters,
    IRepository<Inquiry> inquiries,
    ICampaignRepository campaigns,
    IGuestRepository guests,
    ITemplateRepository templates,
    IEnumerable<IExternalAuthProvider> authProviders,
    IValidator<RegisterDesignerRequest> registerValidator,
    IEnumerable<IOtpSender> otpSenders,
    IOtpService otp,
    IUnitOfWork uow,
    IInviteeTokenIssuer tokenIssuer,
    PhoneNormalizer phones,
    IConfiguration config) : IAccountService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public AuthOptionsDto Options() => new(
        SmsAvailable: SmsIsConfigured(),
        OAuthProviders: authProviders
            .Where(p => p.IsConfigured)
            .Select(p => p.Descriptor())
            .OrderBy(d => d.Provider, StringComparer.Ordinal)
            .Select(d => new OAuthProviderDto(d.Provider, d.ClientId, d.AuthorizeUrl))
            .ToList());

    // ----- Sign up -----------------------------------------------------------------------------

    /// <summary>
    /// Creates a designer account. This is the ONLY self-service way to gain a role beyond Customer,
    /// and it grants exactly one — publishing still goes through admin review, so a new designer can
    /// submit work and nothing else.
    /// <para>
    /// If an account already exists for the email — a customer who has been receiving invitations,
    /// say — it gains the Designer role instead of being refused or duplicated. A password is only
    /// set when the account has none, so this can never overwrite an existing one.
    /// </para>
    /// </summary>
    public async Task<AuthResultDto> RegisterDesignerAsync(
        RegisterDesignerRequest request, CancellationToken ct = default)
    {
        await registerValidator.ValidateAndThrowAsync(request, ct);

        var email = Normalize(request.Email);
        var existing = await LoadAsync(u => u.Email == email, ct);

        if (existing is not null)
        {
            if (!existing.IsActive) throw new AccountSuspendedException();

            // An account with a password is somebody's login: adding a role to it from an anonymous
            // endpoint would let a stranger who knows the address grant themselves that role.
            if (!string.IsNullOrEmpty(existing.PasswordHash))
                throw new BusinessRuleException(
                    "An account already uses that email address. Sign in instead.", "email_taken");

            existing.PasswordHash = PasswordHasher.Hash(request.Password);
            if (!string.IsNullOrWhiteSpace(request.DisplayName))
                existing.DisplayName = request.DisplayName.Trim();
            await AddRoleAsync(existing, Roles.Designer, ct);
            await uow.SaveChangesAsync(ct);

            var upgraded = await LoadAsync(u => u.Id == existing.Id, ct) ?? existing;
            return await IssueAsync(upgraded, ct);
        }

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? email.Split('@')[0]
                : request.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await AddRoleAsync(user, Roles.Designer, ct);
        await AddRoleAsync(user, Roles.Customer, ct);
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        return await IssueAsync(await LoadAsync(u => u.Id == user.Id, ct) ?? user, ct);
    }

    // ----- Sign in -----------------------------------------------------------------------------

    public async Task<AuthResultDto> LoginWithPasswordAsync(
        PasswordLoginRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);
        var user = await LoadAsync(u => u.Email == email, ct);

        // Uniform failure: never reveal whether the account exists.
        if (user is null ||
            string.IsNullOrEmpty(user.PasswordHash) ||
            !PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new SignInFailedException();

        return await IssueAsync(user, ct);
    }

    public async Task<CodeSentResponse> RequestCodeAsync(
        RequestCodeRequest request, CancellationToken ct = default) =>
        await SendCodeAsync(request, ct);

    public async Task<AuthResultDto> VerifyCodeAsync(VerifyCodeRequest request, CancellationToken ct = default)
    {
        var verified = await otp.VerifyContactAsync(new VerifyOtpRequest(request.ChallengeId, request.Code), ct);
        var user = await FindByContactAsync(verified, ct) ?? await CreateFromContactAsync(verified, ct);
        return await IssueAsync(user, ct);
    }

    /// <summary>
    /// Signs in with an ID token the browser got from Google or Microsoft. The token is verified
    /// against the provider's published keys before anything is trusted, and the PROVIDER'S SUBJECT
    /// ID is the linking key — not the email, which a provider could let someone change.
    /// </summary>
    public async Task<AuthResultDto> OAuthAsync(
        string provider, OAuthLoginRequest request, CancellationToken ct = default)
    {
        var impl = authProviders.FirstOrDefault(p =>
                       string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase))
                   ?? throw new NotFoundException($"Unknown sign-in provider '{provider}'.", "oauth_unknown_provider");
        if (!impl.IsConfigured)
            throw new BusinessRuleException(
                $"{provider} sign-in isn't configured on this server.", "oauth_not_configured");

        var identity = await impl.VerifyAsync(request.IdToken, ct);

        var link = await externalLogins.Query(tracking: true)
            .FirstOrDefaultAsync(l => l.Provider == identity.Provider && l.ExternalSubjectId == identity.SubjectId, ct);

        AppUser user;
        if (link is not null)
        {
            user = await LoadAsync(u => u.Id == link.UserId, ct) ?? throw new SignInFailedException();
        }
        else
        {
            // No link yet: attach to whoever owns this VERIFIED email, else create an account. That's
            // what stops someone who signed up with a password from ending up with a second one.
            user = await LoadAsync(u => u.Email == identity.Email, ct)
                   ?? await CreateFromExternalAsync(identity, ct);

            await externalLogins.AddAsync(new UserExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Provider = identity.Provider,
                ExternalSubjectId = identity.SubjectId,
                Email = identity.Email,
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
            await uow.SaveChangesAsync(ct);
        }

        return await IssueAsync(user, ct);
    }

    public async Task<AccountDto> MeAsync(CancellationToken ct = default) =>
        await ToDtoAsync(await CurrentAsync(ct), ct);

    /// <summary>
    /// Turns the account already signed in into a creator's as well. Signing up a second time with
    /// the same address was the only route before, and it doesn't work — the address is taken, and
    /// signing in through Google just returns the customer they already were.
    /// <para>
    /// A fresh token comes back because roles are baked into the one being held: without re-issuing,
    /// the new role wouldn't take effect until the session expired.
    /// </para>
    /// </summary>
    public async Task<AuthResultDto> BecomeDesignerAsync(CancellationToken ct = default)
    {
        var me = await CurrentAsync(ct);
        if (RoleNames(me).Contains(Roles.Designer))
            throw new BusinessRuleException(
                "This account can already publish templates.", "already_a_designer");

        await AddRoleAsync(me, Roles.Designer, ct);
        await uow.SaveChangesAsync(ct);

        return await IssueAsync(await LoadAsync(u => u.Id == me.Id, ct) ?? me, ct);
    }

    // ----- Linking a second identifier ----------------------------------------------------------

    public async Task<CodeSentResponse> RequestLinkCodeAsync(
        RequestCodeRequest request, CancellationToken ct = default)
    {
        await CurrentAsync(ct);   // must be signed in
        return await SendCodeAsync(request, ct);
    }

    public async Task<LinkResultDto> VerifyLinkAsync(VerifyCodeRequest request, CancellationToken ct = default)
    {
        var me = await CurrentAsync(ct);
        var verified = await otp.VerifyContactAsync(new VerifyOtpRequest(request.ChallengeId, request.Code), ct);

        var existing = await FindByContactAsync(verified, ct);
        if (existing is not null && existing.Id == me.Id)
        {
            var unchanged = await IssueAsync(me, ct);
            return new LinkResultDto(unchanged.Account, false, null, unchanged.Token, unchanged.ExpiresAt);
        }

        // An account holds ONE email and ONE phone. Linking a second of the same kind would silently
        // replace the identifier they already sign in with — and if it belonged to another account,
        // quietly strand that one. Refuse plainly instead.
        var current = verified.ContactType == "phone" ? me.PhoneE164 : me.Email;
        if (!string.IsNullOrWhiteSpace(current) &&
            !string.Equals(current, verified.Contact, StringComparison.OrdinalIgnoreCase))
        {
            var kind = verified.ContactType == "phone" ? "phone number" : "email address";
            throw new BusinessRuleException(
                $"This account already uses a different {kind} ({current}). Remove it first, or sign in to the other account and link from there.",
                "identifier_already_set");
        }

        // Attach the identifier to this account…
        if (verified.ContactType == "phone") me.PhoneE164 = verified.Contact;
        else me.Email = verified.Contact;

        // …and absorb the other account, if the identifier already had one.
        string? summary = null;
        if (existing is not null)
        {
            summary = await AbsorbAsync(into: me, from: existing, ct);
        }

        users.Update(me);
        await uow.SaveChangesAsync(ct);

        // Re-issue: the merge may have added roles, and the caller's token predates them.
        var refreshed = await IssueAsync(me, ct);
        return new LinkResultDto(
            refreshed.Account, existing is not null, summary, refreshed.Token, refreshed.ExpiresAt);
    }

    /// <summary>
    /// Folds one account into another: every role it held, every external login, and its identifiers
    /// when this account lacks them. Campaigns and requests need no re-parenting — they're found by
    /// email/phone, and this account now holds both — so nothing about the person's history moves.
    /// </summary>
    private async Task<string> AbsorbAsync(AppUser into, AppUser from, CancellationToken ct)
    {
        var moved = new List<string>();

        // Roles the surviving account doesn't already have. `from.UserRoles` is already tracked (it
        // came back through the same Include), so re-querying would attach a second instance of the
        // same key and EF would refuse. The old rows need no explicit delete either — removing the
        // user cascades them away.
        var mine = into.UserRoles.Select(ur => ur.RoleId).ToHashSet();
        var gained = 0;
        foreach (var role in from.UserRoles.ToList())
            if (mine.Add(role.RoleId))
            {
                into.UserRoles.Add(new UserRole { UserId = into.Id, RoleId = role.RoleId, Role = role.Role });
                gained++;
            }
        if (gained > 0) moved.Add($"{gained} role(s)");

        // Google/Microsoft identities keep working after the merge. These MUST be re-parented before
        // the old user is removed — they cascade-delete with it otherwise — so they're loaded tracked.
        var links = await externalLogins.Query(tracking: true).Where(l => l.UserId == from.Id).ToListAsync(ct);
        foreach (var link in links) link.UserId = into.Id;
        if (links.Count > 0) moved.Add($"{links.Count} linked sign-in(s)");

        // Keep whichever identifiers this account was missing.
        if (string.IsNullOrWhiteSpace(into.Email) && !string.IsNullOrWhiteSpace(from.Email))
            into.Email = from.Email;
        if (string.IsNullOrWhiteSpace(into.PhoneE164) && !string.IsNullOrWhiteSpace(from.PhoneE164))
            into.PhoneE164 = from.PhoneE164;
        if (string.IsNullOrEmpty(into.PasswordHash) && !string.IsNullOrEmpty(from.PasswordHash))
            into.PasswordHash = from.PasswordHash;

        // Anything authored under the old account follows its author (tracked, so the change sticks).
        var authored = await templates.Query(tracking: true)
            .Where(t => t.DesignerUserId == from.Id).ToListAsync(ct);
        foreach (var t in authored) t.DesignerUserId = into.Id;
        if (authored.Count > 0) moved.Add($"{authored.Count} template(s)");

        users.Remove(from);
        return moved.Count == 0 ? "the two accounts are now one" : string.Join(", ", moved) + " moved across";
    }

    // ----- History ------------------------------------------------------------------------------

    public async Task<IReadOnlyList<MyCampaignDto>> MyCampaignsAsync(CancellationToken ct = default)
    {
        var me = await CurrentAsync(ct);
        var inviterIds = await MyInviterIdsAsync(me, ct);

        // Two ways a campaign is yours: you are the host on it, or you started it. The second matters
        // for drafts — the inviter is only attached at the host-details step, so a campaign abandoned
        // before then matches no inviter and would otherwise be invisible to everyone.
        var mine = await campaigns.Query()
            .Where(c => c.CreatedByUserId == me.Id
                        || (c.InviterId != null && inviterIds.Contains(c.InviterId!.Value)))
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);
        if (mine.Count == 0) return [];

        var templateIds = mine.Select(c => c.TemplateId).Distinct().ToList();
        var names = await templates.Query()
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        var result = new List<MyCampaignDto>(mine.Count);
        foreach (var c in mine)
        {
            result.Add(new MyCampaignDto(
                c.Id, c.Title, c.Slug, c.Status.ToString(), c.EventType, c.EventStartAt,
                await guests.CountByCampaignAsync(c.Id, ct),
                names.GetValueOrDefault(c.TemplateId), c.CreatedAt));
        }
        return result;
    }

    public async Task<IReadOnlyList<MyRequestDto>> MyRequestsAsync(CancellationToken ct = default)
    {
        var me = await CurrentAsync(ct);
        if (string.IsNullOrWhiteSpace(me.Email)) return [];

        var mine = await inquiries.Query()
            .Where(i => i.Email == me.Email)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);
        if (mine.Count == 0) return [];

        var issuedIds = mine.Select(i => i.IssuedTemplateId).OfType<Guid>().Distinct().ToList();
        var slugs = issuedIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await templates.Query().Where(t => issuedIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Slug, ct);

        return mine.Select(i => new MyRequestDto(
            i.Id, i.Occasion, i.Message, i.HasAttended, i.TemplateIssued, i.IssuedTemplateId,
            i.IssuedTemplateId is { } id ? slugs.GetValueOrDefault(id) : null,
            i.CreatedAt)).ToList();
    }

    /// <summary>
    /// The inviter records this account speaks for — matched on either identifier, which is what makes
    /// a merged account show the campaigns it made under its phone AND under its email.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> MyInviterIdsAsync(AppUser me, CancellationToken ct)
    {
        var email = me.Email;
        var phone = me.PhoneE164;
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) return [];

        return await inviters.Query()
            .Where(i => (email != null && i.Email == email) || (phone != null && i.PhoneE164 == phone))
            .Select(i => i.Id)
            .ToListAsync(ct);
    }

    // ----- Shared plumbing -----------------------------------------------------------------------

    /// <summary>Sends a code to whichever kind of identifier was typed.</summary>
    private async Task<CodeSentResponse> SendCodeAsync(RequestCodeRequest request, CancellationToken ct)
    {
        var raw = (request.Identifier ?? string.Empty).Trim();
        if (raw.Length == 0) throw new BusinessRuleException("Enter your email or phone number.", "identifier_required");

        if (LooksLikeEmail(raw))
        {
            var email = raw.ToLowerInvariant();
            var challenge = await otp.RequestAsync(new SendOtpRequest("email", null, email, null), ct);
            return new CodeSentResponse(challenge.ChallengeId, "email", MaskEmail(email), challenge.ExpiresInSeconds);
        }

        if (!SmsIsConfigured())
            throw new BusinessRuleException(
                "Signing in by phone isn't switched on yet — use your email address instead.",
                "sms_not_configured");

        var normalized = phones.Normalize(raw, request.DefaultCountry ?? "MV");
        if (!normalized.IsUsable)
            throw new BusinessRuleException("That doesn't look like a valid phone number.", "invalid_phone");

        var sms = await otp.RequestAsync(new SendOtpRequest("sms", normalized.E164, null, request.DefaultCountry), ct);
        return new CodeSentResponse(sms.ChallengeId, "sms", MaskPhone(normalized.E164!), sms.ExpiresInSeconds);
    }

    private async Task<AppUser?> FindByContactAsync(VerifiedContact verified, CancellationToken ct) =>
        verified.ContactType == "phone"
            ? await LoadAsync(u => u.PhoneE164 == verified.Contact, ct)
            : await LoadAsync(u => u.Email == verified.Contact, ct);

    /// <summary>
    /// First sign-in for someone we've only ever sent invitations to. They get the Customer role;
    /// anything more is granted deliberately by an admin.
    /// </summary>
    private async Task<AppUser> CreateFromContactAsync(VerifiedContact verified, CancellationToken ct)
    {
        var isPhone = verified.ContactType == "phone";
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = isPhone ? null : verified.Contact,
            PhoneE164 = isPhone ? verified.Contact : null,
            DisplayName = await DisplayNameForAsync(verified, ct),
            PasswordHash = null,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var customer = await roles.FirstOrDefaultAsync(r => r.Name == Roles.Customer, ct)
                       ?? throw new BusinessRuleException(
                           "The Customer role hasn't been seeded on this server yet.", "customer_role_missing");

        // Reference the role by ID ONLY. That lookup is no-tracking, so attaching the instance as a
        // navigation would make EF treat the role as a new row and try to INSERT it — a duplicate-key
        // failure on every first sign-in. The reload below fills the navigation from the database.
        user.UserRoles.Add(new UserRole { RoleId = customer.Id });

        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        return await LoadAsync(u => u.Id == user.Id, ct) ?? user;
    }

    /// <summary>
    /// First sign-in through a provider with no account here yet. They get the Customer role, exactly
    /// like any other first sign-in — arriving via Google grants nothing extra.
    /// </summary>
    private async Task<AppUser> CreateFromExternalAsync(ExternalIdentity identity, CancellationToken ct)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = identity.Email,
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName)
                ? identity.Email.Split('@')[0]
                : identity.DisplayName,
            PasswordHash = null,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await AddRoleAsync(user, Roles.Customer, ct);
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);
        return await LoadAsync(u => u.Id == user.Id, ct) ?? user;
    }

    /// <summary>
    /// Adds a role by ID only. The role lookup is no-tracking, so attaching the instance as a
    /// navigation would make EF treat it as a new row and try to INSERT it — a duplicate-key failure.
    /// </summary>
    private async Task AddRoleAsync(AppUser user, string roleName, CancellationToken ct)
    {
        var role = await roles.FirstOrDefaultAsync(r => r.Name == roleName, ct)
                   ?? throw new BusinessRuleException(
                       $"The {roleName} role hasn't been seeded on this server yet.", "role_missing");
        if (user.UserRoles.Any(ur => ur.RoleId == role.Id)) return;
        user.UserRoles.Add(new UserRole { RoleId = role.Id });
    }

    /// <summary>Borrows the name they already gave us on an invitation, rather than showing a bare number.</summary>
    private async Task<string> DisplayNameForAsync(VerifiedContact verified, CancellationToken ct)
    {
        var inviter = verified.ContactType == "phone"
            ? await inviters.FirstOrDefaultAsync(i => i.PhoneE164 == verified.Contact, ct)
            : await inviters.FirstOrDefaultAsync(i => i.Email == verified.Contact, ct);
        if (!string.IsNullOrWhiteSpace(inviter?.Name)) return inviter!.Name;

        return verified.ContactType == "email" ? verified.Contact.Split('@')[0] : verified.Contact;
    }

    private async Task<AuthResultDto> IssueAsync(AppUser user, CancellationToken ct)
    {
        if (!user.IsActive) throw new AccountSuspendedException();

        var roleNames = RoleNames(user);
        if (roleNames.Count == 0) roleNames.Add(Roles.Customer);

        var claims = new Dictionary<string, string> { [ClaimTypes.NameIdentifier] = user.Id.ToString() };
        if (!string.IsNullOrWhiteSpace(user.Email)) claims["email"] = user.Email!;
        // The invitee-facing endpoints read the verified contact off the token, so a signed-in
        // customer can open their own invitations without a second OTP round.
        claims[AppContactClaims.ContactType] = string.IsNullOrWhiteSpace(user.Email) ? "phone" : "email";
        claims[AppContactClaims.Contact] = user.Email ?? user.PhoneE164 ?? string.Empty;

        var token = tokenIssuer.IssueForRoles(roleNames, claims, SessionLifetime);
        return new AuthResultDto(token, DateTimeOffset.UtcNow.Add(SessionLifetime), await ToDtoAsync(user, ct));
    }

    private async Task<AppUser> CurrentAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await LoadAsync(u => u.Id == id, ct) ?? throw new UnauthorizedException();
        if (!user.IsActive) throw new AccountSuspendedException();
        return user;
    }

    private Task<AppUser?> LoadAsync(
        System.Linq.Expressions.Expression<Func<AppUser, bool>> predicate, CancellationToken ct) =>
        users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(predicate, ct);

    private async Task<AccountDto> ToDtoAsync(AppUser user, CancellationToken ct)
    {
        var providers = await externalLogins.Query().Where(l => l.UserId == user.Id)
            .Select(l => l.Provider).OrderBy(p => p).ToListAsync(ct);

        return new AccountDto(
            user.Id, user.Email, user.PhoneE164, user.DisplayName, user.IsActive,
            !string.IsNullOrEmpty(user.PasswordHash),
            RoleNames(user).Order().ToList(),
            providers);
    }

    /// <summary>
    /// The role names on an account. A <see cref="UserRole"/> added in memory has no Role navigation
    /// until EF fixes it up, so this reads defensively rather than trusting it to be loaded.
    /// </summary>
    private static List<string> RoleNames(AppUser user) =>
        user.UserRoles.Select(ur => ur.Role?.Name).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    private bool SmsIsConfigured() =>
        otpSenders.Any(s => s.Channel.Equals("sms", StringComparison.OrdinalIgnoreCase))
        && !string.IsNullOrWhiteSpace(config["Sms:MsgOwl:ApiKey"]);

    private static bool LooksLikeEmail(string value) => value.Contains('@');

    private static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

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

    /// <summary>Keeps the country prefix and the last two digits.</summary>
    private static string MaskPhone(string phone)
    {
        if (phone.Length <= 4) return phone;
        return $"{phone[..Math.Min(4, phone.Length)]}•••{phone[^2..]}";
    }
}
