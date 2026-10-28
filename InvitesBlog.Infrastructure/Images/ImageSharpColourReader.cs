using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Campaigns;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InvitesBlog.Infrastructure.Images;

/// <inheritdoc cref="IImageColourReader"/>
public sealed class ImageSharpColourReader : IImageColourReader
{
    /// <summary>
    /// Plenty to find a palette in, and cheap: a design is shrunk to this before it is counted. Not
    /// smaller: at 64 the lettering of a card (often its strongest colour) blurred into the background.
    /// </summary>
    private const int Edge = 256;

    /// <summary>How many of the commonest colours are kept, and the least share of the picture one needs.</summary>
    private const int Keep = 40;
    private const double MinShare = 0.002;
    private const double VividMinShare = 0.001;

    public ImageColours? Read(byte[] content)
    {
        try
        {
            using var image = Image.Load<Rgba32>(content);
            // Nearest-neighbour: every pixel kept is one that was really in the picture, rather than a
            // blend of lettering and background that is in neither.
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(Edge, Edge), Mode = ResizeMode.Max, Sampler = KnownResamplers.NearestNeighbor,
            }));

            // Colours grouped into 4-bit-per-channel bins, each bin remembering the average of what
            // fell into it so a suggestion is a colour that is really in the picture.
            var bins = new Dictionary<int, (long R, long G, long B, int N)>();
            long er = 0, eg = 0, eb = 0; var en = 0;
            var total = 0;
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = image[x, y];
                if (p.A < 128) continue;   // transparent pixels have no colour worth suggesting
                total++;
                var key = (p.R >> 4) << 8 | (p.G >> 4) << 4 | (p.B >> 4);
                var b = bins.GetValueOrDefault(key);
                bins[key] = (b.R + p.R, b.G + p.G, b.B + p.B, b.N + 1);

                // A two-pixel border: what the page around the picture should continue.
                if (x < 2 || y < 2 || x >= image.Width - 2 || y >= image.Height - 2)
                { er += p.R; eg += p.G; eb += p.B; en++; }
            }
            if (total == 0 || en == 0) return null;
            var edge = (R: er / en, G: eg / en, B: eb / en);

            // The vivid colour: strongly coloured pixels, neither near black nor near white, and well
            // apart from the edge. Averaged rather than binned, so the speckle a JPEG leaves around thin
            // lettering still adds up to the lettering's colour.
            long vr = 0, vg = 0, vb = 0; var vn = 0;
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var p = image[x, y];
                if (p.A < 128) continue;
                int max = Math.Max(p.R, Math.Max(p.G, p.B)), min = Math.Min(p.R, Math.Min(p.G, p.B));
                if (max < 60 || max - min < 0.4 * max) continue;
                var dr = p.R - edge.R; var dg = p.G - edge.G; var db = p.B - edge.B;
                if (dr * dr + dg * dg + db * db < 80 * 80) continue;
                vr += p.R; vg += p.G; vb += p.B; vn++;
            }
            // A few stray pixels are noise, not a colour of the design.
            var vivid = vn >= total * VividMinShare ? Hex((int)(vr / vn), (int)(vg / vn), (int)(vb / vn)) : null;

            var dominant = bins.Values
                .Where(b => b.N / (double)total >= MinShare)
                .OrderByDescending(b => b.N)
                .Take(Keep)
                .Select(b => (Hex((int)(b.R / b.N), (int)(b.G / b.N), (int)(b.B / b.N)), b.N / (double)total))
                .ToList();
            return new ImageColours(Hex((int)edge.R, (int)edge.G, (int)edge.B), dominant, vivid);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Hex(int r, int g, int b) => $"#{r:x2}{g:x2}{b:x2}";
}
