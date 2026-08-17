using InvitesBlog.Application.Dtos.Accounts;

namespace InvitesBlog.Application.Services.Accounts;

/// <summary>
/// The one way into the product. Staff, designers and customers all sign in here and are told apart
/// by the ROLES on their account, not by which page they used — so a single person can be an admin,
/// a designer and a customer at once instead of juggling separate logins.
/// </summary>
public interface IAccountService
{
    /// <summary>What the sign-in page can actually offer right now.</summary>
    AuthOptionsDto Options();

    /// <summary>Email + password.</summary>
    Task<AuthResultDto> LoginWithPasswordAsync(PasswordLoginRequest request, CancellationToken ct = default);

    /// <summary>
    /// Self-service sign-up for someone who wants to publish templates. Grants the Designer role and
    /// nothing else — their work still goes through review.
    /// </summary>
    Task<AuthResultDto> RegisterDesignerAsync(RegisterDesignerRequest request, CancellationToken ct = default);

    /// <summary>Signs in with a provider ID token, verified server-side against the provider's keys.</summary>
    Task<AuthResultDto> OAuthAsync(string provider, OAuthLoginRequest request, CancellationToken ct = default);

    /// <summary>Sends a sign-in code to a phone number or an email address.</summary>
    Task<CodeSentResponse> RequestCodeAsync(RequestCodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Verifies a code and signs them in, creating the account on first use — a customer who has only
    /// ever received invitations gets an account the first time they ask for one.
    /// </summary>
    Task<AuthResultDto> VerifyCodeAsync(VerifyCodeRequest request, CancellationToken ct = default);

    Task<AccountDto> MeAsync(CancellationToken ct = default);

    /// <summary>Sends a code to a second identifier the signed-in account wants to add.</summary>
    Task<CodeSentResponse> RequestLinkCodeAsync(RequestCodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Confirms the code and attaches the identifier. If a SEPARATE account already existed for it,
    /// that account is absorbed into this one — roles, external logins and history — and removed, so
    /// the person ends up with one account reachable by either identifier.
    /// </summary>
    Task<LinkResultDto> VerifyLinkAsync(VerifyCodeRequest request, CancellationToken ct = default);

    /// <summary>The invitations this account created, matched on any identifier it holds.</summary>
    Task<IReadOnlyList<MyCampaignDto>> MyCampaignsAsync(CancellationToken ct = default);

    /// <summary>The bespoke-template requests this account made.</summary>
    Task<IReadOnlyList<MyRequestDto>> MyRequestsAsync(CancellationToken ct = default);
}
