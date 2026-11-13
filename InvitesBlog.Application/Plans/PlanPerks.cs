namespace InvitesBlog.Application.Plans;

/// <summary>
/// The bullets on each plan's card, written once here so the website, the apps and the checkout show the same
/// list (pricing 2026-11). Plain sentences with no prices in them, so the apps can show them under the store
/// rules. Built from <see cref="PlanCatalog"/>'s limits, so a changed limit changes its sentence.
///
/// <para>Premium lists every perk the product has for a subscriber ("everything there is", owner 2026-11-13)
/// except the AI designer, which is an add-on (<see cref="AddOns"/>). Studio has it included.</para>
/// </summary>
public static class PlanPerks
{
    public const string NoMarkOnEvents = "No \"Made with invites.blog\" on your events";
    public const string KeptWhileSubscribed = "Everything kept while you're subscribed";
    public const string FacesOnEvents = "Group photos by face on your events";
    public const string DownloadAnywhere = "Download all, or more than 10 at once, in any album you can see";
    public const string FavouritesDownload = "Download all your Favourites at once";
    public const string DesignerCredit = "Your name on the invitations you design";
    public const string DesignerAiIncluded = "The AI designer included";
    public const string Portfolio = "Your own portfolio page at invites.blog/@you";
    public const string BadgeOnEvents = "Your picture and name on your events' albums";

    public static IReadOnlyList<string> For(PlanKind kind) => kind switch
    {
        PlanKind.Free =>
        [
            "Any design, a share link and unlimited replies",
            $"1 album with {Size(PlanCatalog.FreeEventBytes)} for photos and videos",
            "Guests add photos from the day before to the day after",
            "Photos kept for good",
            $"Download or share up to {DownloadRules.Limit} at a time",
            "A small \"Made with invites.blog\" on the invitation",
        ],
        PlanKind.GoldPass =>
        [
            $"{Size(PlanCatalog.GoldEventBytes)} for photos and videos",
            $"{PlanCatalog.GoldBuckets} albums",
            $"Guests add photos for {PlanCatalog.GoldWindowDays} days",
            $"{PlanCatalog.GoldIncludedInvites} emailed invitations",
            "Group photos by face",
            "Download all, for everyone in the event",
            "No \"Made with invites.blog\"",
            "Kept for a year",
        ],
        PlanKind.PlatinumPass =>
        [
            $"{Size(PlanCatalog.PlatinumEventBytes)} for photos and videos",
            $"{PlanCatalog.PlatinumBuckets} albums, private ones too",
            $"Guests add photos for {PlanCatalog.PlatinumWindowDays} days",
            $"{PlanCatalog.PlatinumIncludedInvites} emailed invitations",
            "Group photos by face",
            "Download all, for everyone in the event",
            "No \"Made with invites.blog\"",
            "Kept for a year",
        ],
        PlanKind.Premium =>
        [
            $"{Size(PlanCatalog.PremiumEventBytes)} for every event you organise",
            NoMarkOnEvents,
            KeptWhileSubscribed,
            FacesOnEvents,
            DownloadAnywhere,
            FavouritesDownload,
            DesignerCredit,
        ],
        PlanKind.Venue =>
        [
            $"{Size(PlanCatalog.VenueEventBytes)} for every event you organise",
            BadgeOnEvents,
            Portfolio,
            NoMarkOnEvents,
            KeptWhileSubscribed,
            FacesOnEvents,
            DesignerAiIncluded,
            DownloadAnywhere,
            FavouritesDownload,
        ],
        // Studio without the AI designer (owner, 2026-11-13).
        PlanKind.Photographer =>
        [
            $"{Size(PlanCatalog.VenueEventBytes)} for every event you organise",
            BadgeOnEvents,
            Portfolio,
            NoMarkOnEvents,
            KeptWhileSubscribed,
            FacesOnEvents,
            DownloadAnywhere,
            FavouritesDownload,
        ],
        _ => [],
    };

    private static string Size(long bytes) => bytes >= PlanCatalog.Gb
        ? $"{bytes / (decimal)PlanCatalog.Gb:0.#} GB"
        : $"{bytes / PlanCatalog.Mb} MB";
}
