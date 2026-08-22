namespace InvitesBlog.Application.Dtos.Campaigns;

/// <summary>
/// One thing the RSVP form asks. Four keys are reserved — <c>guestCount</c>, <c>mealPreference</c>,
/// <c>arrivalTime</c> and <c>comment</c> — because those have had their own columns since before the
/// form was configurable, and the dashboard reads them by name. Anything else a host adds is stored
/// as an answer keyed by <see cref="Key"/>.
/// </summary>
/// <param name="Key">Stable id. Renaming a label must not orphan answers already collected.</param>
/// <param name="Type">number | text | textarea | select | yesno</param>
/// <param name="Options">The choices, for <c>select</c> only.</param>
/// <param name="AskIfNotGoing">
/// Most questions are pointless for someone who isn't coming — a head count especially. Off by
/// default so a "sorry, can't make it" stays one tap.
/// </param>
public sealed record RsvpQuestionDto(
    string Key,
    string Label,
    string Type,
    bool Required = false,
    IReadOnlyList<string>? Options = null,
    bool AskIfNotGoing = false);

public sealed record RsvpQuestionsResponse(IReadOnlyList<RsvpQuestionDto> Questions);

public sealed record UpdateRsvpQuestionsRequest(IReadOnlyList<RsvpQuestionDto> Questions);
