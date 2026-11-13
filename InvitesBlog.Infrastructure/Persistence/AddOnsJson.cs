using System.Text.Json;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace InvitesBlog.Infrastructure.Persistence;

/// <summary>How <see cref="AppUser.AddOns"/> is stored: a jsonb list, compared by value so a changed field saves.</summary>
internal static class AddOnsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Write(List<AccountAddOn>? value) => JsonSerializer.Serialize(value ?? [], Options);

    private static List<AccountAddOn> Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<AccountAddOn>>(json, Options) ?? [];

    public static readonly ValueConverter<List<AccountAddOn>, string> Converter = new(v => Write(v), v => Read(v));

    public static readonly ValueComparer<List<AccountAddOn>> Comparer = new(
        (a, b) => Write(a) == Write(b),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
