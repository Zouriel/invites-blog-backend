using System.Reflection;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler.Design;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Publishes the designer's self-hosted fonts to storage (<c>fonts/{id}-{weight}.woff2</c>). They ship
/// inside the API because the render CSP only allows fonts from its own origin; a font CDN would be
/// blocked in every invitation. Idempotent, and cheap enough to run on every boot.
/// </summary>
public sealed class DesignFontSeeder(IStorageService storage, ILogger<DesignFontSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        var names = asm.GetManifestResourceNames();
        var published = 0;
        foreach (var font in DesignCatalog.Fonts)
        {
            foreach (var weight in font.Weights)
            {
                var resource = names.FirstOrDefault(n => n.EndsWith($".Fonts.{font.Id}-{weight}.woff2", StringComparison.Ordinal));
                if (resource is null)
                {
                    logger.LogWarning("Designer font {Font} {Weight} isn't embedded — invitations will fall back.", font.Id, weight);
                    continue;
                }
                await using var stream = asm.GetManifestResourceStream(resource)!;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                await storage.PutAsync(DesignCatalog.FontKey(font.Id, weight), buffer.ToArray(), "font/woff2", ct);
                published++;
            }
        }
        logger.LogInformation("Published {Count} designer font files.", published);
    }
}
