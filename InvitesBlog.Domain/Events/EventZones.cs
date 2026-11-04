namespace InvitesBlog.Domain.Events;

/// <summary>The time zone constants the domain needs; the arithmetic lives in Application.Events.EventTime.</summary>
public static class EventZones
{
    /// <summary>
    /// The zone of every event made before events had one, and the fallback for an account with none.
    /// Only ever a legacy default: clients send the browser's or device's zone for new events.
    /// </summary>
    public const string Legacy = "Indian/Maldives";
}
