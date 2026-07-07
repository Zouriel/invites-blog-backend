using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>Seeds the default template types (§4.5.1). Idempotent — admins add more via the portal.</summary>
public sealed class TemplateTypeSeeder(AppDbContext db)
{
    private static readonly string[] Defaults =
    {
        "Wedding", "Engagement", "Anniversary", "Birthday", "Baby Shower",
        "Graduation", "Ceremony", "Religious Event", "Corporate Event", "Conference",
        "Workshop", "Launch Event", "Private Dinner", "Custom Event"
    };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var existing = (await db.TemplateTypes.Select(t => t.Slug).ToListAsync(ct)).ToHashSet();
        var order = 0;
        foreach (var name in Defaults)
        {
            order += 10;
            var slug = Slugify(name);
            if (existing.Contains(slug)) continue;
            db.TemplateTypes.Add(new TemplateType
            {
                Id = Guid.NewGuid(), Name = name, Slug = slug,
                SortOrder = order, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync(ct);
    }

    internal static string Slugify(string input)
    {
        var slug = new string(input.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
