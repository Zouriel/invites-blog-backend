namespace InvitesBlog.Application.Abstractions;

/// <param name="Kinds">What can be searched for there: vector, animated, picture.</param>
/// <param name="Available">False when the source needs an account the server doesn't have configured.</param>
public sealed record ArtSourceDto(string Id, string Name, bool Available, IReadOnlyList<string> Kinds, string Note);

/// <param name="Kind">svg | gif | image — what the file is, which says whether it can move.</param>
/// <param name="TooLarge">Known to be bigger than the importer will download.</param>
public sealed record ArtItemDto(
    string Source, string Id, string Title, string Thumb, string Kind, string? Creator, string License,
    string? PageUrl, int? Width, int? Height, bool TooLarge);

public sealed record ArtSearchResultDto(IReadOnlyList<ArtItemDto> Items, int Page, bool HasMore, int? Total);

public sealed record ArtImportRequest(string Source, string Id, string? Title);

/// <summary>A keyframe of an imported layer: T through its track, offsets in the art's own units.</summary>
public sealed record ArtFrameDto(double T, double Dx, double Dy, double Rotate, double Scale, double Opacity);

public sealed record ArtLayerDto(string Asset, string? Name, IReadOnlyList<ArtFrameDto> Frames);

public sealed record ArtCreditDto(string Source, string? Creator, string License, string? PageUrl);

/// <summary>
/// An illustration ready to place: its assets, and layers back to front that each cover the whole
/// <see cref="Width"/>×<see cref="Height"/> box. Animated art comes with keyframes against a scroll track.
/// </summary>
/// <param name="Seconds">How long the original animation ran over the track.</param>
/// <param name="Loops">How many times a repeating animation plays over the track.</param>
/// <param name="Bytes">What it adds to the published page, roughly.</param>
/// <param name="AsPicture">A vector too detailed for an invitation, brought in as the library's own picture of it instead.</param>
public sealed record ArtImportDto(
    string Name, double Width, double Height, IReadOnlyList<DesignAssetDto> Assets, IReadOnlyList<ArtLayerDto> Layers,
    bool Animated, double Seconds, int Loops, ArtCreditDto? Credit, int Bytes, bool AsPicture = false);

/// <summary>
/// Free illustration libraries for the designer — searched and imported on the server, so the browser
/// never fetches third-party files into a design, and every file is cleaned before a scene sees it.
/// Only public-domain (CC0 / PDM) work is offered: a template is sold on, so nothing that needs
/// attribution or share-alike can go into one.
/// </summary>
public interface IArtLibrary
{
    IReadOnlyList<ArtSourceDto> Sources();

    Task<ArtSearchResultDto> SearchAsync(string source, string? query, string? kind, int page, CancellationToken ct = default);

    Task<ArtImportDto> ImportAsync(ArtImportRequest request, CancellationToken ct = default);

    /// <summary>A file the designer uploaded, through the same conversion (animations become scroll motion).</summary>
    ArtImportDto ImportFile(byte[] content, string fileName, string contentType);
}
