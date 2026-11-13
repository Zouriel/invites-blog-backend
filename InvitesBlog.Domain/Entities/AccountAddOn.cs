namespace InvitesBlog.Domain.Entities;

/// <summary>
/// One add-on on an account's plan (stored in <see cref="AppUser.AddOns"/>). It only counts while the plan it
/// belongs to is in force: the AI designer is an add-on to Premium.
/// </summary>
public sealed class AccountAddOn
{
    /// <summary>Which add-on: "designer-ai".</summary>
    public string Key { get; set; } = "";

    /// <summary>When what was paid for it ends; null for no end (given with a Premium that has no end).</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>It is part of the plan's next renewal (charged with it, or the store renews the plan with it).</summary>
    public bool Renews { get; set; }

    /// <summary>Where it came from: "bml", "google_play", "app_store", "admin" or "carried-over" (Premium before 2026-11-13).</summary>
    public string? Source { get; set; }
}
