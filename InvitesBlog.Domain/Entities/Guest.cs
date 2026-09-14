namespace InvitesBlog.Domain.Entities;

/// <summary>A campaign guest (§8.2 Guest). Identity is phone_e164 / email.</summary>
public sealed class Guest
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string? Email { get; set; }
    public string? PhoneE164 { get; set; }
    public string? PhoneRaw { get; set; }
    public string Name { get; set; } = "Guest";

    /// <summary>
    /// The guest's first role. Kept beside <see cref="Roles"/> because templates already pinned to a
    /// campaign bind <c>guest.role</c> as one value, and the theme and role-scoped fields pick one.
    /// Always set through <see cref="SetRoles"/> so the two cannot disagree.
    /// </summary>
    public string? Role { get; set; }

    /// <summary>
    /// Every role the guest holds, in the order the host picked them. Usually one; several when one
    /// invitation goes to a household ("Ali and family" is both the groom family's men and women, and
    /// should get both sets of dress colours). Empty on rows written before this existed — read
    /// through <see cref="AllRoles"/>, which falls back to <see cref="Role"/>.
    /// </summary>
    public List<string> Roles { get; set; } = new();

    public string Gender { get; set; } = "unspecified";
    public string MetadataJson { get; set; } = "{}";
    public bool OptedOut { get; set; }                 // §15.3
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Every role, or the single legacy <see cref="Role"/> for a row that predates the list.</summary>
    public IReadOnlyList<string> AllRoles() =>
        Roles.Count > 0 ? Roles
        : string.IsNullOrWhiteSpace(Role) ? Array.Empty<string>()
        : new[] { Role };

    public void SetRoles(IEnumerable<string?>? roles)
    {
        Roles = NormalizeRoles(roles);
        Role = Roles.FirstOrDefault();
    }

    /// <summary>Trimmed, blanks dropped, duplicates (ignoring case) removed, order kept.</summary>
    public static List<string> NormalizeRoles(IEnumerable<string?>? roles) =>
        (roles ?? Array.Empty<string?>())
            .Select(r => r?.Trim())
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
