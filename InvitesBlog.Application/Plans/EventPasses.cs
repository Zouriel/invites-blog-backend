using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>Putting a pass on an event, the one way an admin, a checkout and a Studio giving one to a client all do it.</summary>
public static class EventPasses
{
    /// <summary>Whether the event has a pass in force right now, and which.</summary>
    public static EventPassKind Active(Campaign campaign, DateTimeOffset now) =>
        campaign.EventPass != EventPassKind.None && campaign.EventPassUntil is { } until && until > now
            ? campaign.EventPass
            : EventPassKind.None;

    /// <summary>
    /// Gives the event a pass, or with <see cref="EventPassKind.None"/> takes it away. A pass runs a
    /// year from the event day, or from today if it has passed. The same pass again adds a year;
    /// moving up from Party to Wedding keeps whichever end is later; a smaller pass never replaces a
    /// bigger one still in force.
    /// </summary>
    public static void Apply(Campaign campaign, EventPassKind kind, DateTimeOffset now)
    {
        if (kind == EventPassKind.None)
        {
            // Ended rather than erased, so the photos' lapse counts from today.
            if (campaign.EventPassUntil is { } end && end > now) campaign.EventPassUntil = now;
            campaign.EventPass = EventPassKind.None;
            return;
        }

        var current = Active(campaign, now);
        var fresh = PlanRules.PassUntil(campaign.EventStartAt, now);
        if (current == kind)
            campaign.EventPassUntil = campaign.EventPassUntil!.Value.AddMonths(PlanCatalog.PassMonths);
        else if (current == EventPassKind.None)
            campaign.EventPassUntil = fresh;
        else if (campaign.EventPassUntil < fresh)
            campaign.EventPassUntil = fresh;

        if (kind > current) campaign.EventPass = kind;
        campaign.UpdatedAt = now;
    }
}
