namespace InvitesBlog.Application.Abstractions;

/// <summary>What a template declares, flattened to plain strings for the layers above.</summary>
public sealed record TemplateStructure(
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> ImageSlots,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ThemeKeys);

public sealed record DesignIssueDto(string Severity, string Code, string Message, string? ElementId);

/// <param name="Sample">filled | empty | roles — the invitation the page is bound with.</param>
/// <param name="Blocks">Role-specific blocks to show; null shows all of them.</param>
/// <param name="Hidden">Element ids muted in the editor's timeline.</param>
/// <param name="Scroll">Where the editor preview opens, in canvas units.</param>
public sealed record DesignBuildOptions(
    string Title = "Invitation",
    bool EditorPreview = false,
    string? Sample = null,
    IReadOnlyCollection<string>? Blocks = null,
    IReadOnlySet<string>? Hidden = null,
    double Scroll = 0);

/// <param name="Html">The compiled document — bound with sample data when a sample was asked for.</param>
/// <param name="Bytes">Size of the UNBOUND compile, which is what is published and what the limits count.</param>
/// <param name="Errors">True when any issue blocks publishing.</param>
public sealed record DesignBuild(
    string Html,
    int Bytes,
    IReadOnlyList<DesignIssueDto> Issues,
    TemplateStructure Structure,
    bool Errors);

public sealed record DesignAssetDto(
    string Id, string Kind, string Data, double Width, double Height,
    IReadOnlyList<string>? Colors, string Name, int Bytes, bool? Alpha = null);

/// <summary>What tinting a picture needs: whether it has see-through pixels, and for an opaque one its brightness mask.</summary>
public sealed record ImageTintInfoDto(bool Alpha, string? Mask);

public sealed record AnalyseImageRequest(string Data);

/// <summary>
/// An asset to make smaller for the largest size it's shown at in the design (<paramref name="Width"/> x
/// <paramref name="Height"/> CSS pixels, the picture's own box, after fit and scale).
/// </summary>
public sealed record ShrinkAssetRequest(string Id, string Kind, string Data, double Width, double Height, string? Name);

/// <summary>
/// The visual designer's compiler, validator and catalog, as the Application layer sees them. The
/// scene crosses this boundary as JSON so no layer above Infrastructure depends on the compiler's
/// model — the same arrangement <see cref="ITemplatePackager"/> has.
/// </summary>
public interface IDesignEngine
{
    /// <summary>Fonts, variables, presets, starters and limits, for the editor.</summary>
    object Catalog();

    /// <summary>
    /// Parses and normalises a scene: unknown properties dropped, shape enforced. Throws a
    /// BusinessRuleException when it can't be read at all.
    /// </summary>
    string Normalize(string sceneJson);

    /// <summary>Compiles and checks. Never throws for a readable scene — problems come back as issues.</summary>
    DesignBuild Build(string sceneJson, DesignBuildOptions options);

    /// <summary>A starter scene as JSON, or null for an unknown id.</summary>
    string? Starter(string id);

    /// <summary>True when a template's stored scene came from the designer (and can be opened exactly).</summary>
    bool IsDesignedScene(string? sceneJson);

    /// <summary>Sanitises an uploaded SVG, or shrinks a picture, into an asset the scene can embed.</summary>
    DesignAssetDto ImportAsset(byte[] content, string fileName, string contentType);

    /// <summary>
    /// The asset made as small as it can be without looking any different at the size it's shown: a picture
    /// resized to twice that (sharp on a 2x screen) and re-encoded; an illustration's coordinates rounded to
    /// what that size can show. Hands back the asset as it was when nothing would be gained.
    /// </summary>
    DesignAssetDto ShrinkAsset(ShrinkAssetRequest request);

    /// <summary>Whether a picture has see-through pixels, and for an opaque one the brightness mask a silhouette tint paints through.</summary>
    ImageTintInfoDto AnalyseImage(string data);

    /// <summary>
    /// Prepares a hand-written template for conversion: bound with sample data and carrying the
    /// extractor script, which rebuilds a scene in the editor's browser.
    /// </summary>
    string ImportDocument(string packagedHtml);
}
