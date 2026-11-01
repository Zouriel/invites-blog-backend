using SixLabors.ImageSharp.Metadata.Profiles.Exif;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// Takes the camera's notes out of a JPEG without touching a single pixel: GPS and the rest live in marker
/// segments in front of the picture data, so they can be cut out and the picture left exactly as encoded.
/// </summary>
internal static class JpegMetadata
{
    private const byte Marker = 0xFF;
    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sos = 0xDA;
    private const byte App1 = 0xE1;
    private const byte App2 = 0xE2;
    private const byte App13 = 0xED;
    private const byte Comment = 0xFE;

    /// <summary>Whether the tag asks for the picture to be turned or flipped.</summary>
    public static bool HasTurn(this ExifProfile? exif)
    {
        var tag = exif?.Values.FirstOrDefault(v => v.Tag == ExifTag.Orientation);
        return tag?.GetValue() is ushort turn && turn > 1;
    }

    /// <summary>
    /// The file without its Exif, XMP, IPTC, comments or anything hung on after the picture (an Ultra HDR
    /// gain map, whose description is in the XMP going with it). The colour profile stays, because it
    /// changes how the picture looks. False if the file isn't laid out as a JPEG should be.
    /// </summary>
    public static bool TryStrip(byte[] jpeg, out byte[] clean)
    {
        clean = jpeg;
        if (jpeg.Length < 4 || jpeg[0] != Marker || jpeg[1] != Soi) return false;

        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        var at = 2;
        while (true)
        {
            if (at + 4 > jpeg.Length || jpeg[at] != Marker) return false;
            var marker = jpeg[at + 1];
            if (marker == Marker) { at++; continue; } // Fill byte.
            if (marker == Eoi) return false;          // A picture with no picture data.

            var length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (length < 2 || at + 2 + length > jpeg.Length) return false;

            if (marker == Sos)
            {
                var end = EndOfPicture(jpeg, at + 2 + length);
                if (end < 0) return false;
                output.Write(jpeg, at, end - at);
                clean = output.ToArray();
                return true;
            }

            if (!Drops(jpeg, at, marker)) output.Write(jpeg, at, 2 + length);
            at += 2 + length;
        }
    }

    private static bool Drops(byte[] jpeg, int at, byte marker)
    {
        if (marker is App1 or App13 or Comment) return true;
        if (marker != App2) return false;
        // Keep the colour profile; MPF describes the gain map, and that's gone.
        const string icc = "ICC_PROFILE";
        if (at + 4 + icc.Length > jpeg.Length) return true;
        for (var i = 0; i < icc.Length; i++)
            if (jpeg[at + 4 + i] != icc[i]) return true;
        return false;
    }

    /// <summary>Just past the end marker. A later scan starts another segment, which is walked over too.</summary>
    private static int EndOfPicture(byte[] jpeg, int from)
    {
        for (var i = from; i + 1 < jpeg.Length; i++)
        {
            if (jpeg[i] != Marker) continue;
            var next = jpeg[i + 1];
            if (next == Eoi) return i + 2;
        }
        return -1;
    }
}
