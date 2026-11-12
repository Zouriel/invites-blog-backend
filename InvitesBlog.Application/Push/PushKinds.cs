namespace InvitesBlog.Application.Push;

/// <summary>
/// What a notification is about. Travels in every push's data as <c>kind</c>, picks the Android channel
/// the phone files it under (people can silence a channel in the phone's settings), and decides which
/// in-app switch can mute it (<see cref="MuteGroup"/>).
/// </summary>
public static class PushKinds
{
    /// <summary>The host sent the invitations: "You're invited".</summary>
    public const string Invitation = "invitation";
    /// <summary>Added as one of the people an event is for.</summary>
    public const string Added = "added";
    /// <summary>The day before an event a guest said they're coming to.</summary>
    public const string Reminder = "reminder";
    /// <summary>A guest answered an invitation.</summary>
    public const string Reply = "reply";
    /// <summary>A comment on an event's post, or a reply to one.</summary>
    public const string Comment = "comment";
    /// <summary>New photos and videos in an album.</summary>
    public const string Photos = "photos";
    /// <summary>Hearts on photos in an album.</summary>
    public const string Hearts = "hearts";
    /// <summary>Face grouping news for an album's host: grouping finished, someone opted out.</summary>
    public const string Faces = "faces";
    /// <summary>Things that are about to happen to an album: photos above Free removed, face grouping ending.</summary>
    public const string Album = "album";

    /// <summary>Data keys every push carries next to <c>path</c>.</summary>
    public const string KindKey = "kind";
    public const string ThreadKey = "thread";
    public const string CollapseKey = "collapse";
    public const string PathKey = "path";

    /// <summary>
    /// The switches people can turn off in the app. Invitations, being added, album notices and face grouping
    /// notices can't be muted here: they are rare and say something is about to happen to the person's own
    /// things (the phone's own settings can still silence anything).
    /// </summary>
    public static readonly IReadOnlyList<string> Mutable = ["replies", "comments", "photos", "hearts", "reminders"];

    /// <summary>The in-app switch that mutes a kind, or null when it can't be muted.</summary>
    public static string? MuteGroup(string? kind) => kind switch
    {
        Reply => "replies",
        Comment => "comments",
        Photos => "photos",
        Hearts => "hearts",
        Reminder => "reminders",
        _ => null,
    };

    /// <summary>
    /// The Android notification channel (app: Push.kt). Channels the installed app hasn't created yet fall
    /// back to the manifest's default, <c>events</c>, so older app versions still show everything.
    /// </summary>
    public static string AndroidChannel(string? kind) => kind switch
    {
        Reminder => "reminders",
        Reply => "replies",
        Comment => "comments",
        Photos or Hearts => "photos",
        Faces or Album => "album",
        _ => "events",
    };

    /// <summary>
    /// The data a push carries: its path, its kind, and optionally a thread (notifications about one event or
    /// album are grouped together) and a collapse key (a newer one with the same key replaces the older one).
    /// </summary>
    public static Dictionary<string, string> Data(string path, string kind, string? thread = null, string? collapse = null)
    {
        var data = new Dictionary<string, string> { [PathKey] = path, [KindKey] = kind };
        if (!string.IsNullOrWhiteSpace(thread)) data[ThreadKey] = thread;
        if (!string.IsNullOrWhiteSpace(collapse)) data[CollapseKey] = collapse;
        return data;
    }
}
