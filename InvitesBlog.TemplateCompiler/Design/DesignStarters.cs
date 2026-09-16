using System.Text.Json.Serialization;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// The scenes the editor opens with. Built in code rather than shipped as JSON so they cannot drift
/// from the schema — a starter that failed Check would be the first thing every customer saw.
/// </summary>
public static class DesignStarters
{
    public sealed record Starter(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description);

    public static readonly IReadOnlyList<Starter> All =
    [
        new("blank", "Blank page", "An empty page with the essentials: title, date, venue and RSVP."),
        new("wedding", "Classic wedding", "Gold on ivory, a cover photo that holds while the details rise past it."),
        new("birthday", "Birthday party", "Bold colours and confetti shapes that spin as you scroll."),
        new("save-the-date", "Save the date", "One screen, big type, straight to the point."),
    ];

    public static DesignScene? Create(string id) => id switch
    {
        "blank" => Blank(),
        "wedding" => Wedding(),
        "birthday" => Birthday(),
        "save-the-date" => SaveTheDate(),
        _ => null,
    };

    // ----- Starters ----------------------------------------------------------------------------------

    private static DesignScene Blank()
    {
        var s = Scene(3, "#b08d57", "#fbf7f0", "#2b2622", "playfair-display", "inter");
        var y = 0.0;
        s.Elements.Add(Text("title", 30, 300, 330, 90, [Var("event.title")], Heading(40)));
        s.Elements.Add(Text("subtitle", 30, 400, 330, 40, [Var("event.subtitle")], Body(18, italic: true)));
        y = 844;
        s.Elements.Add(Text("date", 30, y + 250, 330, 40, [Var("event.date")], Body(20, color: "theme:accent")));
        s.Elements.Add(Text("time", 30, y + 292, 330, 32, [Var("event.time")], Body(16)));
        s.Elements.Add(Text("venue", 30, y + 380, 330, 36, [Var("event.venue.name")], Heading(24)));
        s.Elements.Add(Text("address", 30, y + 420, 330, 50, [Var("event.venue.address")], Body(14)));
        y = 1688;
        s.Elements.Add(Text("dear", 30, y + 200, 330, 40, [Lit("Dear "), Var("guest.name")], Body(18)));
        s.Elements.Add(Rsvp("rsvp", 95, y + 280, 200, 52));
        s.Elements.Add(Dress("dress", 30, y + 400, 330, 120));
        foreach (var el in s.Elements) FadeUp(s, el);
        return s;
    }

    private static DesignScene Wedding()
    {
        var s = Scene(4, "#b08d57", "#fbf7f0", "#3a3129", "cormorant-garamond", "lora");
        s.Fonts = ["cormorant-garamond", "lora", "playfair-display", "great-vibes"];
        s.Theme.Add(new DesignThemeEntry { Key = "script-font", Label = "Script font", Value = "great-vibes" });
        s.Canvas.Sections[1].Background = "theme:accent";

        // Hero: the photo holds while the names rise over it.
        var photo = Slot("cover", 0, 0, 390, 844, "event.coverImage", "Cover photo");
        photo.Pinned = true;
        photo.Track = new DesignTrack { Start = 0, End = 844 };
        photo.Keyframes = [new() { T = 0, Scale = 1.08 }, new() { T = 1, Scale = 1, Opacity = 0.2 }];
        s.Elements.Add(photo);

        var veil = Shape("veil", 0, 520, 390, 324, "rect", "theme:bg");
        veil.Opacity = 0.92;
        s.Elements.Add(veil);
        s.Elements.Add(Text("names", 20, 560, 350, 100, [Var("event.title")], Heading(46, font: "theme:script-font", color: "theme:accent")));
        s.Elements.Add(Text("sub", 20, 660, 350, 36, [Var("event.subtitle")], Body(17, italic: true)));
        s.Elements.Add(Shape("rule1", 145, 712, 100, 2, "line", stroke: "theme:accent"));

        // Section 2: date card on the accent ground.
        var y = 844.0;
        s.Elements.Add(Text("save", 30, y + 180, 330, 30, [Lit("Together with their families")], Body(14, color: "theme:bg", uppercase: true, spacing: 0.2)));
        s.Elements.Add(Text("date", 30, y + 300, 330, 60, [Var("event.date")], Heading(34, color: "theme:bg")));
        s.Elements.Add(Text("time", 30, y + 370, 330, 30, [Lit("at "), Var("event.time")], Body(18, color: "theme:bg")));
        var ring = Shape("ring", 95, y + 230, 200, 200, "ellipse", stroke: "theme:bg");
        ring.Shape!.StrokeWidth = 1.5;
        ring.Shape.Fill = null;
        ring.Opacity = 0.5;
        ring.Track = new DesignTrack { Start = y - 422, End = y + 422 };
        ring.Keyframes = [new() { T = 0, Scale = 0.6, Opacity = 0 }, new() { T = 0.5, Scale = 1.6, Opacity = 0.5 }, new() { T = 1, Scale = 2.4, Opacity = 0 }];
        s.Elements.Add(ring);

        // Section 3: venue + dress colours.
        y = 1688;
        s.Elements.Add(Text("where", 30, y + 120, 330, 30, [Lit("Where")], Body(14, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements.Add(Text("venue", 30, y + 160, 330, 50, [Var("event.venue.name")], Heading(32)));
        s.Elements.Add(Text("address", 40, y + 215, 310, 60, [Var("event.venue.address")], Body(15)));
        s.Elements.Add(Link("map", 120, y + 290, 150, 40, "event.venue.mapLink", "Open map"));
        s.Elements.Add(Text("wear", 30, y + 420, 330, 30, [Lit("What to wear")], Body(14, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements.Add(Dress("dress", 30, y + 460, 330, 140));

        // Section 4: the ask.
        y = 2532;
        s.Elements.Add(Text("dear", 30, y + 160, 330, 90, [Lit("Dear "), Var("guest.name"), Lit(",\nwe would be honoured by your presence.")], Body(20, italic: true)));
        s.Elements.Add(Rsvp("rsvp", 85, y + 290, 220, 54));
        s.Elements.Add(Link("camera", 85, y + 360, 220, 44, "camera.link", "Share your photos"));
        s.Elements.Add(Text("tag", 30, y + 460, 330, 30, [Var("event.hashtag")], Body(15, color: "theme:accent")));

        foreach (var el in s.Elements.Where(e => e.Id is not ("cover" or "ring" or "veil"))) FadeUp(s, el);
        return s;
    }

    private static DesignScene Birthday()
    {
        var s = Scene(3, "#ff5d73", "#1d1a3a", "#fdf6ff", "poppins", "poppins");
        s.Fonts = ["poppins", "montserrat", "dancing-script"];
        s.Theme.Add(new DesignThemeEntry { Key = "pop", Label = "Pop colour", Value = "#ffd166" });
        s.Theme.Add(new DesignThemeEntry { Key = "cool", Label = "Cool colour", Value = "#4cc9f0" });

        (string Kind, double X, double Y, double Size, string Color, double Spin)[] confetti =
        [
            ("polygon", 30, 80, 46, "theme:pop", 180), ("ellipse", 300, 140, 34, "theme:cool", 0),
            ("rect", 250, 60, 26, "theme:accent", 220), ("polygon", 60, 640, 38, "theme:cool", -160),
            ("ellipse", 320, 700, 50, "theme:pop", 0), ("rect", 180, 760, 22, "theme:pop", 300),
        ];
        var i = 0;
        foreach (var c in confetti)
        {
            var piece = Shape($"confetti{i++}", c.X, c.Y, c.Size, c.Size, c.Kind, c.Color);
            if (c.Kind == "rect") piece.Shape!.Radius = 6;
            piece.Shape!.Sides = 3;
            piece.Track = new DesignTrack { Start = 0, End = 900 };
            piece.Keyframes = [new() { T = 0, Rotate = 0 }, new() { T = 1, Rotate = c.Spin, Y = c.Y + 260 }];
            s.Elements.Add(piece);
        }

        s.Elements.Add(Text("hey", 30, 230, 330, 40, [Lit("You're invited!")], Body(22, font: "dancing-script", color: "theme:pop")));
        s.Elements.Add(Text("title", 20, 280, 350, 150, [Var("event.title")], Heading(56, weight: 600, uppercase: true)));
        s.Elements.Add(Text("sub", 30, 440, 330, 40, [Var("event.subtitle")], Body(18)));

        var y = 844.0;
        var card = Shape("card", 30, y + 150, 330, 380, "rect", "theme:accent");
        card.Shape!.Radius = 28;
        card.Track = new DesignTrack { Start = y - 500, End = y + 200 };
        card.Keyframes = [new() { T = 0, Rotate = -8, Scale = 0.8, Opacity = 0 }, new() { T = 0.6, Rotate = 0, Scale = 1, Opacity = 1, Easing = "ease-out" }, new() { T = 1 }];
        s.Elements.Add(card);
        s.Elements.Add(Text("when", 50, y + 190, 290, 30, [Lit("When")], Body(14, color: "theme:bg", uppercase: true, spacing: 0.2)));
        s.Elements.Add(Text("date", 50, y + 225, 290, 70, [Var("event.date")], Heading(28, weight: 600, color: "theme:bg")));
        s.Elements.Add(Text("time", 50, y + 295, 290, 30, [Var("event.time")], Body(18, color: "theme:bg")));
        s.Elements.Add(Text("where", 50, y + 360, 290, 30, [Lit("Where")], Body(14, color: "theme:bg", uppercase: true, spacing: 0.2)));
        s.Elements.Add(Text("venue", 50, y + 395, 290, 40, [Var("event.venue.name")], Heading(24, weight: 600, color: "theme:bg")));
        s.Elements.Add(Text("address", 50, y + 438, 290, 60, [Var("event.venue.address")], Body(14, color: "theme:bg")));

        y = 1688;
        s.Elements.Add(Text("dear", 30, y + 150, 330, 60, [Lit("See you there, "), Var("guest.name"), Lit("!")], Heading(26, weight: 600)));
        var rsvp = Rsvp("rsvp", 80, y + 250, 230, 60);
        rsvp.Button!.Fill = "theme:pop";
        rsvp.Button.Style.Color = "theme:bg";
        s.Elements.Add(rsvp);
        s.Elements.Add(Link("camera", 80, y + 330, 230, 48, "camera.link", "Party camera"));
        s.Elements.Add(Dress("dress", 30, y + 420, 330, 130));

        foreach (var el in s.Elements.Where(e => !e.Id.StartsWith("confetti", StringComparison.Ordinal) && e.Id != "card")) FadeUp(s, el, "pop");
        return s;
    }

    private static DesignScene SaveTheDate()
    {
        var s = Scene(2, "#1b3d59", "#f3eed8", "#152026", "italiana", "josefin-sans");
        s.Fonts = ["italiana", "josefin-sans", "cinzel"];
        s.Elements.Add(Text("kicker", 30, 170, 330, 30, [Lit("Save the date")], Body(15, uppercase: true, spacing: 0.35, color: "theme:accent")));
        s.Elements.Add(Text("title", 20, 230, 350, 120, [Var("event.title")], Heading(54)));
        s.Elements.Add(Shape("rule", 170, 370, 50, 2, "line", stroke: "theme:accent"));
        s.Elements.Add(Text("date", 20, 400, 350, 60, [Var("event.date")], Heading(30, color: "theme:accent")));
        s.Elements.Add(Text("venue", 30, 470, 330, 40, [Var("event.venue.name")], Body(16, uppercase: true, spacing: 0.15)));
        s.Elements.Add(Text("later", 30, 1100, 330, 60, [Lit("Formal invitation to follow")], Body(15, italic: true)));
        s.Elements.Add(Rsvp("rsvp", 95, 1180, 200, 52));
        s.Elements.Add(Dress("dress", 30, 1290, 330, 110));
        foreach (var el in s.Elements) FadeUp(s, el, "fade");
        return s;
    }

    // ----- Builders ----------------------------------------------------------------------------------

    private static DesignScene Scene(int sections, string accent, string bg, string text, string headingFont, string bodyFont) => new()
    {
        Canvas = new DesignCanvas
        {
            Sections = Enumerable.Range(1, sections)
                .Select(i => new DesignSection { Id = $"sec{i}", Name = $"Screen {i}", Height = DesignCanvas.ReferenceViewport })
                .ToList(),
        },
        Theme =
        [
            new() { Key = "accent", Label = "Accent", Value = accent },
            new() { Key = "bg", Label = "Background", Value = bg },
            new() { Key = "text", Label = "Text", Value = text },
            new() { Key = "heading-font", Label = "Heading font", Value = headingFont },
            new() { Key = "body-font", Label = "Body font", Value = bodyFont },
        ],
        Fonts = [headingFont, bodyFont],
    };

    private static DesignRun Var(string path) => new() { Var = path };
    private static DesignRun Lit(string text) => new() { Text = text };

    private static DesignTypography Heading(double size, string font = "theme:heading-font", string? color = null, int weight = 400, bool uppercase = false) =>
        new() { Font = font, Size = size, Weight = weight, Color = color ?? "theme:text", LineHeight = 1.1, Uppercase = uppercase };

    private static DesignTypography Body(double size, bool italic = false, string? color = null, bool uppercase = false, double spacing = 0, string font = "theme:body-font") =>
        new() { Font = font, Size = size, Italic = italic, Color = color ?? "theme:text", Uppercase = uppercase, LetterSpacing = spacing, LineHeight = 1.4 };

    private static DesignElement Text(string id, double x, double y, double w, double h, List<DesignRun> runs, DesignTypography style) => new()
    {
        Id = id, Type = "text", Name = Title(id), X = x, Y = y, W = w, H = h,
        Text = new DesignText { Runs = runs, Style = style },
    };

    private static DesignElement Shape(string id, double x, double y, double w, double h, string kind, string? fill = null, string? stroke = null) => new()
    {
        Id = id, Type = "shape", Name = Title(id), X = x, Y = y, W = w, H = Math.Max(h, kind == "line" ? 4 : h),
        Shape = new DesignShape { Kind = kind, Fill = fill, Stroke = stroke, StrokeWidth = stroke is null ? 0 : 1.5 },
    };

    private static DesignElement Slot(string id, double x, double y, double w, double h, string path, string label) => new()
    {
        Id = id, Type = "slot", Name = label, X = x, Y = y, W = w, H = h,
        Slot = new DesignSlot { Path = path, Label = label },
    };

    private static DesignElement Rsvp(string id, double x, double y, double w, double h) => new()
    {
        Id = id, Type = "rsvp", Name = "RSVP button", X = x, Y = y, W = w, H = h,
        Button = new DesignButton
        {
            Fill = "theme:accent", Radius = 999,
            Style = new DesignTypography { Font = "theme:body-font", Size = 17, Weight = 600, Color = "theme:bg", LetterSpacing = 0.04 },
        },
    };

    private static DesignElement Link(string id, double x, double y, double w, double h, string path, string label) => new()
    {
        Id = id, Type = "link", Name = label, X = x, Y = y, W = w, H = h,
        Button = new DesignButton
        {
            Path = path, Label = label, Stroke = "theme:accent", StrokeWidth = 1, Radius = 999,
            Style = new DesignTypography { Font = "theme:body-font", Size = 15, Color = "theme:accent" },
        },
    };

    private static DesignElement Dress(string id, double x, double y, double w, double h) => new()
    {
        Id = id, Type = "dress", Name = "Dress colours", X = x, Y = y, W = w, H = h,
        Dress = new DesignDress { Style = new DesignTypography { Font = "theme:body-font", Size = 14, Color = "theme:text", Uppercase = true, LetterSpacing = 0.12 } },
    };

    /// <summary>Applies an enter preset over a track that starts as the element comes up the screen.</summary>
    private static void FadeUp(DesignScene scene, DesignElement el, string preset = "fade-up")
    {
        if (el.Keyframes.Count > 0) return;
        // What's on the opening screen is there when the invitation opens; only what's below it rises in.
        if (el.Y + el.H * 0.5 < DesignCanvas.ReferenceViewport * 0.9) return;
        var start = Math.Max(0, el.Y - DesignCanvas.ReferenceViewport * 0.95);
        el.Track = new DesignTrack { Start = start, End = start + 1400 };
        DesignPresets.Apply(el, DesignCatalog.EnterPresets.First(p => p.Id == preset), "enter");
    }

    private static string Title(string id) => char.ToUpperInvariant(id[0]) + id[1..];
}

/// <summary>Materialises a motion preset as ordinary keyframes — the same thing the editor does when you pick one.</summary>
public static class DesignPresets
{
    public static void Apply(DesignElement el, DesignCatalog.Preset preset, string slot)
    {
        el.Keyframes.RemoveAll(k => k.Preset == slot);
        foreach (var f in preset.Frames)
        {
            el.Keyframes.Add(new DesignKeyframe
            {
                T = f.T,
                X = f.Dx == 0 ? null : el.X + f.Dx,
                Y = f.Dy == 0 ? null : el.Y + f.Dy,
                Rotate = f.DRotate == 0 ? null : el.Rotate + f.DRotate,
                Scale = Math.Abs(f.ScaleFactor - 1) < 0.0001 ? null : el.Scale * f.ScaleFactor,
                Opacity = Math.Abs(f.OpacityFactor - 1) < 0.0001 ? null : el.Opacity * f.OpacityFactor,
                Easing = f.Easing,
                Preset = slot,
            });
        }
        // Resting values made explicit at the preset's far end, so carry-forward can't leave an
        // enter's starting offset in place for the rest of the track.
        var last = el.Keyframes.Where(k => k.Preset == slot).MaxBy(k => slot == "enter" ? k.T : -k.T);
        if (last is not null)
        {
            last.X ??= el.X; last.Y ??= el.Y; last.Rotate ??= el.Rotate; last.Scale ??= el.Scale; last.Opacity ??= el.Opacity;
        }
        el.Keyframes.Sort((a, b) => a.T.CompareTo(b.T));
        if (slot == "enter") el.Enter = preset.Id; else el.Exit = preset.Id;
    }
}
