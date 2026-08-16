namespace InvitesBlog.Application.Abstractions;

/// <summary>Issues signed JWTs for OTP-verified invitees and for admin sessions. Implemented in Infrastructure.</summary>
public interface IInviteeTokenIssuer
{
    /// <summary>Invitee inbox token (role defaults to Invitee).</summary>
    string Issue(string contactType, string contact, TimeSpan lifetime);

    /// <summary>A token carrying a specific role + arbitrary claims (e.g. an Admin session).</summary>
    string IssueForRole(string role, IReadOnlyDictionary<string, string> claims, TimeSpan lifetime);

    /// <summary>
    /// A token carrying EVERY role the account holds. One person is often several things at once —
    /// an admin who also authors templates, a designer who is also a customer — and a single-role
    /// token would force them to pick one identity per sign-in.
    /// </summary>
    string IssueForRoles(IReadOnlyCollection<string> roles, IReadOnlyDictionary<string, string> claims, TimeSpan lifetime);
}
