using System.Globalization;
using InvitesBlog.Domain.Events;

namespace InvitesBlog.Application.Events;

/// <summary>
/// An event's clock. Event times are stored as instants (timestamptz, always UTC in the database);
/// every place that shows one to a person, or asks "which day is it", goes through here with the
/// event's own IANA zone, so a London wedding reads 7:00 pm GMT to a guest in New York as well.
///
/// <para>Zones are IANA ids only ("Europe/London"). Linux containers resolve them natively; a Windows
/// id ("GMT Standard Time") is refused even though .NET could translate it, so stored values stay in
/// one spelling every client understands.</para>
/// </summary>
public static class EventTime
{
    /// <summary>True when <paramref name="id"/> is an IANA time zone this server knows.</summary>
    public static bool IsValidZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        // An IANA id has a Windows equivalent; a Windows id doesn't convert from IANA, so this also
        // refuses "GMT Standard Time". UTC is IANA too ("UTC", "Etc/UTC").
        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out _);
    }

    /// <summary>The zone for <paramref name="id"/>, or the legacy Maldives zone when it is missing or unknown.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(EventZones.Legacy, out var legacy)) return legacy;
        // A host without the zone database still gets Malé's clock: +05:00, no daylight saving.
        return TimeZoneInfo.CreateCustomTimeZone(EventZones.Legacy, TimeSpan.FromHours(5), EventZones.Legacy, EventZones.Legacy);
    }

    /// <summary>The zone id to store: <paramref name="id"/> when valid, else <paramref name="fallback"/>, else the legacy zone.</summary>
    public static string ZoneOrDefault(string? id, string? fallback = null) =>
        IsValidZone(id) ? id!.Trim() : IsValidZone(fallback) ? fallback!.Trim() : EventZones.Legacy;

    /// <summary><paramref name="at"/> on the zone's clock, with the offset in force at that moment.</summary>
    public static DateTimeOffset Local(DateTimeOffset at, string? zoneId) =>
        TimeZoneInfo.ConvertTime(at, Zone(zoneId));

    /// <summary>The calendar day <paramref name="at"/> falls on in the zone.</summary>
    public static DateTime Day(DateTimeOffset at, string? zoneId) => Local(at, zoneId).Date;

    /// <summary>
    /// Midnight at the start of <paramref name="day"/> in the zone, as an instant. Where a daylight
    /// saving change skips midnight, the first instant of that day.
    /// </summary>
    public static DateTimeOffset StartOfDay(DateTime day, string? zoneId)
    {
        var zone = Zone(zoneId);
        var local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        // An ambiguous midnight (clocks went back over it) takes the earlier one: the day starts then.
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    /// <summary>
    /// A short label for the zone at that moment: "GMT" at offset zero, otherwise the offset itself
    /// ("+05", "+05:30", "-04"). Abbreviations like BST/EST aren't in the zone database .NET reads,
    /// and an offset is unambiguous in every language.
    /// </summary>
    public static string Label(DateTimeOffset at, string? zoneId) => OffsetLabel(Local(at, zoneId).Offset);

    public static string OffsetLabel(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "GMT";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return abs.Minutes == 0
            ? $"{sign}{abs.Hours:00}"
            : $"{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>"Saturday, 24 October 2026", the day in the event's zone.</summary>
    public static string DateText(DateTimeOffset at, string? zoneId) =>
        Local(at, zoneId).ToString("dddd, d MMMM yyyy", English);

    /// <summary>"7:00 pm GMT": the time on the event's clock with its zone label.</summary>
    public static string TimeText(DateTimeOffset at, string? zoneId)
    {
        var local = Local(at, zoneId);
        return $"{local.ToString("h:mm", CultureInfo.InvariantCulture)} {(local.Hour < 12 ? "am" : "pm")} {OffsetLabel(local.Offset)}";
    }
}
