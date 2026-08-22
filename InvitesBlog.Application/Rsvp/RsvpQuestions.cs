using System.Text.Json;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Rsvp;

/// <summary>
/// Reads and writes a campaign's RSVP question set, and knows what to ask when a host hasn't said.
/// </summary>
public static class RsvpQuestions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string GuestCount = "guestCount";
    public const string MealPreference = "mealPreference";
    public const string ArrivalTime = "arrivalTime";
    public const string Comment = "comment";

    /// <summary>Keys that live in their own column rather than in the answers bag.</summary>
    public static readonly IReadOnlySet<string> Reserved =
        new HashSet<string>([GuestCount, MealPreference, ArrivalTime, Comment], StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> Types =
        ["number", "text", "textarea", "select", "yesno"];

    /// <summary>
    /// What the form asked before it was configurable. Campaigns created back then have no question
    /// set stored, and answering them with an empty form would quietly drop questions their guests
    /// were already being asked.
    /// </summary>
    public static IReadOnlyList<RsvpQuestionDto> Defaults() =>
    [
        new(GuestCount, "How many guests? (including you)", "number"),
        new(MealPreference, "Meal preference", "text"),
        new(ArrivalTime, "Arrival time", "text"),
        new(Comment, "A note for the host", "textarea", AskIfNotGoing: true),
    ];

    public static IReadOnlyList<RsvpQuestionDto> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Defaults();
        try
        {
            var parsed = JsonSerializer.Deserialize<RsvpQuestionsResponse>(json, Json);
            // An empty list means "never configured", not "ask nothing" — a host who wants no extra
            // questions still leaves the going/not-going choice, which isn't in this list.
            return parsed?.Questions is { Count: > 0 } q ? q : Defaults();
        }
        catch (JsonException)
        {
            return Defaults();
        }
    }

    public static string Serialize(IReadOnlyList<RsvpQuestionDto> questions) =>
        JsonSerializer.Serialize(new RsvpQuestionsResponse(questions), Json);
}
