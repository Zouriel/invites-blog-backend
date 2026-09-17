namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Brings a scene saved by an older editor up to <see cref="DesignScene.CurrentSchema"/>, so it compiles
/// the way it did when it was made. Runs on every parse; a current scene passes through untouched.
/// </summary>
public static class DesignSceneUpgrade
{
    public static void Apply(DesignScene scene)
    {
        if (scene.Schema == 2) FromScreens(scene);
    }

    /// <summary>
    /// Schema 2 → 3: the page was a stack of screens and is now as long as its content.
    ///
    /// <list type="bullet">
    /// <item>A coloured screen becomes a full-width box at the back, so the colour stays where it was.</item>
    /// <item>Tracks were clamped to the old page's scroll range when compiled; they're clamped for good
    /// here, so motion keeps its timing when the page's length changes.</item>
    /// <item>Motion with no track played over the whole old page; that range is written down for the same reason.</item>
    /// </list>
    /// </summary>
    private static void FromScreens(DesignScene scene)
    {
        var sections = scene.Canvas.Sections ?? [];
        var oldRange = Math.Max(0, sections.Where(s => double.IsFinite(s.Height)).Sum(s => s.Height) - DesignCanvas.ReferenceViewport);

        foreach (var (el, _, _) in scene.Walk())
        {
            if (el.Track is { } t && double.IsFinite(t.Start) && double.IsFinite(t.End))
            {
                var start = Math.Clamp(t.Start, 0, oldRange);
                var end = Math.Clamp(t.End, 0, oldRange);
                el.Track = new DesignTrack { Start = start, End = end > start ? end : start + 1 };
            }
            else if (el.Track is null && (el.Keyframes.Count > 0 || el.Pinned) && oldRange > 0)
                el.Track = new DesignTrack { Start = 0, End = oldRange };
        }

        var backgrounds = new List<DesignElement>();
        double top = 0;
        var ids = scene.Walk().Select(w => w.Element.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var height = double.IsFinite(section.Height) ? Math.Max(0, section.Height) : 0;
            if (section.Background is not null && height > 0)
            {
                var id = "bg" + new string((section.Id ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(40).ToArray());
                while (!ids.Add(id)) id += "x";
                backgrounds.Add(new DesignElement
                {
                    Id = id, Type = "shape", Name = $"{section.Name} background".Trim(), X = 0, Y = top, W = DesignCanvas.Width, H = height,
                    Shape = new DesignShape { Kind = "rect", Fill = section.Background },
                });
            }
            top += height;
        }
        scene.Elements.InsertRange(0, backgrounds);

        scene.Canvas.Sections = null;
        scene.Schema = DesignScene.CurrentSchema;
    }
}
