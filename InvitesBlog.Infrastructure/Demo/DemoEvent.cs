using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Dtos.Feed;
using InvitesBlog.Application.Dtos.MediaBuckets;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Rsvp;

namespace InvitesBlog.Infrastructure.Demo;

/// <summary>
/// GET /api/demo/event: the organiser dashboard (<see cref="DashboardResponse"/>, the shape of
/// GET /api/me/campaigns/{id}/dashboard) with <c>demo: true</c>, plus what the dashboard screen fetches
/// separately, each in its own endpoint's shape, so the apps render it with their real screens:
/// <list type="bullet">
/// <item><c>celebrants</c>: GET /api/campaigns/{id}/celebrants (<see cref="CelebrantDto"/>[]).</item>
/// <item><c>buckets</c>: GET /api/campaigns/{id}/buckets (<see cref="MediaBucketDto"/>[]); the one album.</item>
/// <item><c>album</c>: GET /api/media-buckets/{id}/media (<see cref="EventPhotoBoxDto"/>).</item>
/// <item><c>covers</c>: GET /api/me/feed/{id}/covers (<see cref="FeedCoversDto"/>).</item>
/// </list>
/// Everything in it is invented and fixed (docs/GUIDANCE.md section 7). Nothing in it can be written to:
/// every id starts with <see cref="IdPrefix"/> and exists in no table, there are no tokens, share links,
/// open links or contacts, and every "can" flag is false.
/// </summary>
public sealed record DemoEventResponse(
    bool Demo,
    DashboardCampaignDto Campaign,
    DashboardReportDto Report,
    IReadOnlyList<DashboardGuestDto> Guests,
    IReadOnlyList<RsvpQuestionDto> RsvpQuestions,
    SendingAllowanceDto Sending,
    IReadOnlyList<CelebrantDto> Celebrants,
    IReadOnlyList<MediaBucketDto> Buckets,
    EventPhotoBoxDto Album,
    FeedCoversDto Covers);

public sealed class DemoEventBuilder(IStorageService storage)
{
    /// <summary>Every id in the demo starts with this. Real ids are random (version 4); none can.</summary>
    public const string IdPrefix = "de300000-0000-4000-8000-";

    public const string Title = "Aisha & Ibrahim";

    /// <summary>How far ahead the wedding always is.</summary>
    public const int DaysAhead = 21;

    private const string Zone = "Indian/Maldives";

    /// <summary>A fixed, recognisable id: <see cref="IdPrefix"/> + a 12-digit number.</summary>
    public static Guid Id(int n) => Guid.Parse($"{IdPrefix}{n:D12}");

    private static readonly Guid CampaignId = Id(1);
    private static readonly Guid BucketId = Id(2);

    // Name, role, reply (Going/Maybe/NotGoing/NoResponse), opened, party size, comment.
    private static readonly (string Name, string Role, string Reply, bool Opened, int? Count, string? Comment)[] People =
    [
        ("Mariyam Hassan", "Bride's family", "Going", true, 2, "Wouldn't miss it!"),
        ("Ahmed Rasheed", "Groom's family", "Going", true, 4, null),
        ("Fathimath Shifa", "Friends", "Going", true, 1, "So happy for you both"),
        ("Hussain Naseem", "Groom's family", "Going", true, 2, null),
        ("Aminath Riya", "Bride's family", "Going", true, 3, null),
        ("Ibrahim Shareef", "Friends", "Maybe", true, 1, "Flying in that week, will confirm"),
        ("Hawwa Leena", "Friends", "Going", true, 2, null),
        ("Mohamed Zahir", "Colleagues", "NotGoing", true, null, "Sorry, I'm away. Congratulations!"),
        ("Aishath Nazima", "Bride's family", "Going", true, 2, null),
        ("Ali Waheed", "Groom's family", "Going", true, 5, null),
        ("Sara Ibrahim", "Friends", "Maybe", true, 2, null),
        ("Yoosuf Adam", "Colleagues", "Going", true, 1, null),
        ("Khadeeja Anwar", "Bride's family", "Going", true, 2, "Can't wait!"),
        ("Hassan Fikry", "Groom's family", "NotGoing", true, null, null),
        ("Nadhiya Ahmed", "Friends", "Going", true, 2, null),
        ("Ismail Rameez", "Colleagues", "NoResponse", true, null, null),
        ("Mausooma Ali", "Bride's family", "Going", true, 3, null),
        ("Shiyam Moosa", "Friends", "NoResponse", false, null, null),
        ("Raufa Hameed", "Groom's family", "Maybe", true, 2, null),
        ("Abdulla Naseer", "Colleagues", "NoResponse", false, null, null),
        ("Zeena Shakir", "Friends", "Going", true, 1, null),
        ("Imran Saeed", "Groom's family", "NoResponse", false, null, null),
        ("Lamya Hussain", "Bride's family", "NoResponse", true, null, null),
        ("Faisal Mohamed", "Friends", "NoResponse", false, null, null),
    ];

    public DemoEventResponse Build(DateTimeOffset now)
    {
        // Fixed for the whole day (so the response caches well): 5 pm in Malé, three weeks out.
        var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(DaysAhead);
        var startsAt = day.AddHours(12);
        var made = day.AddDays(-45);

        var photos = DemoAssets.Photos;
        var photoDtos = photos.Select((p, i) => new EventPhotoDto(
            Id(1000 + i),
            storage.PublicUrl(DemoAssets.Key(p)),
            storage.PublicUrl(DemoAssets.ThumbKey(p)),
            storage.PublicUrl(DemoAssets.Key(p)),
            "image/webp", p.Width, p.Height,
            UploaderName: People[(i * 5) % People.Length].Name,
            CanDelete: false,
            CreatedAt: now.UtcDateTime.Date.AddDays(-1 - i / 4).AddHours(10 + i % 8),
            UploaderUserId: null,
            SizeBytes: p.Bytes,
            FavouriteCount: (i * 7 + 3) % 6,
            Favourited: false,
            People: null)).ToList();
        var cover = photoDtos.Count > 0 ? photoDtos[0].Url : null;
        var usedBytes = photos.Sum(p => p.Bytes);

        var guests = People.Select((g, i) => new DashboardGuestDto(
            Id(100 + i), g.Name,
            Email: null, PhoneE164: null,
            Role: g.Role, Gender: "unspecified", OptedOut: false,
            InviteStatus: g.Opened ? "Viewed" : "Sent",
            RsvpStatus: g.Reply,
            ViewedAt: g.Opened ? made.AddDays(20 + i % 9).AddHours(i) : null,
            DeliveryChannel: "email",
            RsvpAnswers: g.Reply == "NoResponse" ? null : Answers(g.Count, g.Comment),
            Roles: [g.Role],
            SelfAdded: false,
            TypedContact: null,
            EmailStatus: "Delivered",
            EmailAt: made.AddDays(18),
            EmailError: null,
            ManualSends: new Dictionary<string, DateTimeOffset>(),
            ShareLink: null,
            ShareText: null)).ToList();

        var report = new DashboardReportDto(
            Total: guests.Count,
            Sent: guests.Count,
            Failed: 0,
            Viewed: People.Count(p => p.Opened),
            NotSent: 0,
            Rsvp: new DashboardRsvpDto(
                Going: People.Count(p => p.Reply == "Going"),
                Maybe: People.Count(p => p.Reply == "Maybe"),
                NotGoing: People.Count(p => p.Reply == "NotGoing")));

        var roles = """{"roles":[{"name":"Bride's family","contentBlocks":[]},{"name":"Groom's family","contentBlocks":[]},{"name":"Friends","contentBlocks":[]},{"name":"Colleagues","contentBlocks":[]}]}""";

        var campaign = new DashboardCampaignDto(
            CampaignId, Title, "Dispatched", PaidInviteCapacity: 0, RolesJson: roles,
            CoverImageUrl: cover,
            TemplatePreviewImageUrl: null,
            HasInvitation: true,
            OpenLink: null,
            IsImported: false,
            IsDraft: false,
            ResumeStep: null,
            Viewer: "organiser",
            Kind: "invitation",
            AllDay: false,
            EventStartAt: startsAt,
            InvitationCampaignId: null,
            OpenGuestList: false,
            TimeZone: Zone);

        var celebrants = new List<CelebrantDto>
        {
            new(Id(50), "Aisha Ahmed", Email: null, Phone: null, CanManage: true, NotifiedAt: made.AddDays(1)),
            new(Id(51), "Ibrahim Nasheed", Email: null, Phone: null, CanManage: false, NotifiedAt: made.AddDays(1)),
        };

        var bucket = new MediaBucketDto(
            BucketId, Name: "Photos", Title: Title, CoverUrl: cover, Tier: "GoldPass",
            Gb: Math.Round(PlanCatalog.GoldEventBytes / (double)PlanCatalog.Gb, 1),
            CapacityBytes: PlanCatalog.GoldEventBytes, UsedBytes: usedBytes,
            PercentUsed: (int)Math.Round(100.0 * usedBytes / PlanCatalog.GoldEventBytes),
            ItemCount: photoDtos.Count, CampaignId: CampaignId, CampaignTitle: Title,
            EventDate: startsAt, IsOpen: false, WindowDays: PlanCatalog.GoldWindowDays, IsDefault: true,
            TermEndAt: null, Expired: false, CreatedAt: made,
            MaxBuckets: PlanCatalog.GoldBuckets, MaxWindowDays: PlanCatalog.GoldWindowDays,
            Phase: "Active", EventUsedBytes: usedBytes, PrivateAlbums: false, Branded: false,
            VenueName: null, VenueLogoUrl: null, CardTheme: null, TrimAt: null, TrimBytes: null,
            FreeBytes: PlanCatalog.FreeEventBytes, FaceGrouping: false, FaceGroupingAvailable: false, FaceGroupingIncluded: true);

        var album = new EventPhotoBoxDto(
            CampaignId, Title, photoDtos.Count, CanUpload: false, photoDtos,
            ClosedNote: "This is an example event. Nothing here is real.");

        var covers = new FeedCoversDto(BucketId, photoDtos.Take(2).Select(p => p.Id).ToList(), 6);

        return new DemoEventResponse(
            Demo: true, campaign, report, guests, RsvpQuestions.Defaults(),
            new SendingAllowanceDto(PlanCatalog.GoldIncludedInvites, 0, guests.Count),
            celebrants, [bucket], album, covers);
    }

    private static Dictionary<string, string> Answers(int? count, string? comment)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (count is { } c) answers[RsvpQuestions.GuestCount] = c.ToString();
        if (!string.IsNullOrWhiteSpace(comment)) answers[RsvpQuestions.Comment] = comment;
        return answers;
    }
}
