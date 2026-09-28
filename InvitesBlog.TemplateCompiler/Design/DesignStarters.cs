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
        new("blank", "Blank page", "Nothing on it yet. Start from scratch."),
        new("wedding", "Classic wedding", "Gold on ivory, a cover photo that holds while the details rise past it."),
        new("birthday", "Birthday party", "Bold colours and confetti shapes that spin as you scroll."),
        new("save-the-date", "Save the date", "One screen, big type, straight to the point."),
        new("envelope", "Envelope", "A sealed envelope that opens as you scroll — or when the seal is tapped — and the card rises out."),
    ];

    public static DesignScene? Create(string id) => id switch
    {
        "blank" => Blank(),
        "wedding" => Wedding(),
        "birthday" => Birthday(),
        "save-the-date" => SaveTheDate(),
        "envelope" => Envelope(),
        _ => null,
    };

    // ----- Starters ----------------------------------------------------------------------------------

    /// <summary>
    /// Truly blank: the page's colours and fonts, and nothing on it. (Check asks for an RSVP button before
    /// it can be published — that's the one thing every invitation needs, and the editor says so.)
    /// </summary>
    private static DesignScene Blank()
    {
        // New designs start on a stage: things stay where they're put until they're animated.
        var s = Scene("#b08d57", "#fbf7f0", "#2b2622", "playfair-display", "inter");
        s.Stage = true;
        return s;
    }

    private static DesignScene Wedding()
    {
        var s = Scene("#b08d57", "#fbf7f0", "#3a3129", "cormorant-garamond", "lora");
        s.Fonts = ["cormorant-garamond", "lora", "playfair-display", "great-vibes"];
        s.Theme.Add(new DesignThemeEntry { Key = "script-font", Label = "Script font", Value = "great-vibes" });

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

        // Second screen: date card on the accent ground.
        var y = 844.0;
        var ground = Shape("ground", 0, y, 390, 844, "rect", "theme:accent");
        ground.Name = "Accent background";
        s.Elements.Insert(0, ground);
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

        // Third: venue + dress colours.
        y = 1688;
        s.Elements.Add(Text("where", 30, y + 120, 330, 30, [Lit("Where")], Body(14, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements.Add(Text("venue", 30, y + 160, 330, 50, [Var("event.venue.name")], Heading(32)));
        s.Elements.Add(Text("address", 40, y + 215, 310, 60, [Var("event.venue.address")], Body(15)));
        s.Elements.Add(Link("map", 120, y + 290, 150, 40, "event.venue.mapLink", "Open map"));
        s.Elements.Add(Text("wear", 30, y + 420, 330, 30, [Lit("What to wear")], Body(14, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements.Add(Dress("dress", 30, y + 460, 330, 140));

        // Last: the ask.
        y = 2532;
        s.Elements.Add(Text("dear", 30, y + 160, 330, 90, [Lit("Dear "), Var("guest.name"), Lit(",\nwe would be honoured by your presence.")], Body(20, italic: true)));
        s.Elements.Add(Rsvp("rsvp", 85, y + 290, 220, 54));
        s.Elements.Add(Link("camera", 85, y + 360, 220, 44, "camera.link", "Share your photos"));
        s.Elements.Add(Text("tag", 30, y + 460, 330, 30, [Var("event.hashtag")], Body(15, color: "theme:accent")));

        foreach (var el in s.Elements.Where(e => e.Id is not ("cover" or "ring" or "veil" or "ground"))) FadeUp(s, el);
        return s;
    }

    private static DesignScene Birthday()
    {
        var s = Scene("#ff5d73", "#1d1a3a", "#fdf6ff", "poppins", "poppins");
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
        var s = Scene("#1b3d59", "#f3eed8", "#152026", "italiana", "josefin-sans");
        s.Kind = "saveTheDate";
        s.Fonts = ["italiana", "josefin-sans", "cinzel"];
        s.Elements.Add(Text("kicker", 30, 170, 330, 30, [Lit("Save the date")], Body(15, uppercase: true, spacing: 0.35, color: "theme:accent")));
        s.Elements.Add(Text("title", 20, 230, 350, 120, [Var("event.title")], Heading(54)));
        s.Elements.Add(Shape("rule", 170, 370, 50, 2, "line", stroke: "theme:accent"));
        s.Elements.Add(Text("date", 20, 400, 350, 60, [Var("event.date")], Heading(30, color: "theme:accent")));
        s.Elements.Add(Text("venue", 30, 470, 330, 40, [Var("event.venue.name")], Body(16, uppercase: true, spacing: 0.15)));
        // No reply button and no dress colours: a save the date asks nothing yet. "Add to calendar"
        // is added by the server at the foot of every save the date.
        s.Elements.Add(Text("later", 30, 1100, 330, 60, [Lit("Invitation to follow")], Body(15, italic: true)));
        foreach (var el in s.Elements) FadeUp(s, el, "fade");
        return s;
    }

    // ----- Builders ----------------------------------------------------------------------------------

    private static DesignScene Scene(string accent, string bg, string text, string headingFont, string bodyFont) => new()
    {
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

    /// <summary>
    /// The envelope opener every invitation shop sells, built from ordinary elements: it stays put for its
    /// first screen and a half of scroll while the seal cracks, the flap folds back (a 3D turn about its
    /// top edge), and the card rises out and comes to the front. Tapping the seal scrolls there for you.
    /// </summary>
    private static DesignScene Envelope()
    {
        var s = Scene("#8c2f39", "#f4ece2", "#3b2a2a", "cormorant-garamond", "lora");
        s.Fonts = ["cormorant-garamond", "lora", "great-vibes", "playfair-display"];
        s.Theme.Add(new DesignThemeEntry { Key = "script-font", Label = "Script font", Value = "great-vibes" });
        s.Theme.Add(new DesignThemeEntry { Key = "paper", Label = "Card", Value = "#fffaf2" });
        s.Theme.Add(new DesignThemeEntry { Key = "gold", Label = "Seal", Value = "#c9a45c" });
        s.Theme.Add(new DesignThemeEntry { Key = "flap", Label = "Envelope flap", Value = "#6f2330" });

        const double hold = 1100; // scroll the opening plays over, while the envelope stays on screen
        DesignTrack Opening() => new() { Start = 0, End = hold };
        DesignElement Pin(DesignElement el) { el.Pinned = true; el.Track = Opening(); return el; }
        static DesignPath Outline(double w, double h, params (double X, double Y)[] points) => new()
        {
            Width = w, Height = h,
            Contours = [new DesignContour { Closed = true, Points = points.Select(p => new DesignPathPoint { X = p.X, Y = p.Y }).ToList() }],
        };
        // The envelope goes once the card is out.
        List<DesignKeyframe> Leave(double y) => [new() { T = 0.72, Y = y, Opacity = 1 }, new() { T = 0.9, Y = y + 260, Opacity = 0, Easing = "ease-in" }];

        s.Elements.Add(Text("hello", 30, 110, 330, 40, [Lit("You have a letter")], Body(15, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements[^1].Track = Opening();
        s.Elements[^1].Pinned = true;
        s.Elements[^1].Keyframes = [new() { T = 0, Opacity = 1 }, new() { T = 0.12, Opacity = 0 }];

        var back = Pin(Shape("back", 40, 250, 310, 220, "rect", "theme:accent"));
        back.Name = "Envelope back";
        back.Shape!.Radius = 6;
        back.Keyframes = Leave(250);
        s.Elements.Add(back);

        var card = Pin(new DesignElement
        {
            Id = "card", Type = "group", Name = "Card", X = 60, Y = 275, W = 270, H = 190,
            Children =
            [
                new DesignElement
                {
                    Id = "paper", Type = "shape", Name = "Card paper", X = 0, Y = 0, W = 270, H = 190,
                    Shape = new DesignShape { Kind = "rect", Fill = "theme:paper", Radius = 6, Stroke = "theme:gold", StrokeWidth = 1 },
                },
                Text("together", 15, 22, 240, 20, [Lit("Together with their families")], Body(11, color: "theme:accent", uppercase: true, spacing: 0.18)),
                Text("names", 10, 48, 250, 80, [Var("event.title")], Heading(40, font: "theme:script-font", color: "theme:accent")),
                Text("when", 15, 136, 240, 26, [Var("event.date")], Body(15)),
            ],
        });
        // Rises out once the flap is open, then comes in front of everything and settles larger.
        card.Keyframes =
        [
            new() { T = 0.34, Y = 275, Scale = 1, Lift = 0 },
            new() { T = 0.56, Y = 120, Scale = 1, Lift = 0, Easing = "ease-out" },
            new() { T = 0.6, Lift = 20 },
            new() { T = 0.78, Y = 250, Scale = 1.22, Lift = 20, Easing = "ease-in-out" },
        ];
        s.Elements.Add(card);

        // The front folds in from the top corners, so with the flap shut nothing inside shows.
        var pocket = Pin(Shape("pocket", 40, 250, 310, 220, "path", "theme:accent"));
        pocket.Name = "Envelope front";
        pocket.Shape!.Path = Outline(310, 220, (0, 0), (155, 136), (310, 0), (310, 220), (0, 220));
        pocket.Keyframes = Leave(250);
        s.Elements.Add(pocket);

        // The flap: folds back about its top edge. In front while closed, behind the card once open.
        var flap = Pin(Shape("flap", 40, 250, 310, 150, "path", "theme:flap"));
        flap.Name = "Envelope flap";
        flap.Shape!.Path = Outline(310, 150, (0, 0), (310, 0), (155, 150));
        flap.Origin = new DesignOrigin { X = 0.5, Y = 0 };
        flap.Keyframes =
        [
            new() { T = 0.08, RotateX = 0, Lift = 10, Easing = "ease-in-out" },
            new() { T = 0.3, RotateX = 180, Lift = 10 },
            new() { T = 0.31, Lift = 0 },
            .. Leave(250),
        ];
        s.Elements.Add(flap);

        // The wax seal: swells, then breaks. Tapping it plays the opening for you.
        var seal = Pin(Shape("seal", 165, 368, 60, 60, "ellipse", "theme:gold"));
        seal.Name = "Seal";
        seal.TapScroll = hold * 0.62;
        seal.Keyframes = [new() { T = 0, Scale = 1 }, new() { T = 0.05, Scale = 1.15, Easing = "ease-out" }, new() { T = 0.09, Scale = 0.6, Opacity = 0, Easing = "ease-in" }];
        s.Elements.Add(seal);
        var initial = Pin(Text("initial", 165, 380, 60, 36, [Lit("&")], Heading(26, font: "theme:script-font", color: "theme:accent")));
        initial.Keyframes = [new() { T = 0, Opacity = 1 }, new() { T = 0.06, Opacity = 0 }];
        s.Elements.Add(initial);

        var hint = Pin(Text("hint", 45, 700, 300, 30, [Lit("Scroll, or tap the seal")], Body(14, italic: true, color: "theme:accent")));
        hint.Keyframes = [new() { T = 0, Opacity = 1 }, new() { T = 0.08, Opacity = 0 }];
        hint.Loop = new DesignLoop
        {
            Preset = "bob", Repeat = 6,
            Frames = [new() { T = 0, Easing = "ease-out" }, new() { T = 0.5, Dy = 8, Easing = "ease-in" }, new() { T = 1 }],
        };
        s.Elements.Add(hint);

        // After the opening: the details, then the reply.
        var y = 844 + hold;
        s.Elements.Add(Text("dear", 30, y + 40, 330, 60, [Lit("Dear "), Var("guest.name"), Lit(",")], Heading(30)));
        s.Elements.Add(Text("invite", 30, y + 110, 330, 90, [Var("event.description")], Body(16)));
        s.Elements.Add(Text("where-l", 30, y + 230, 330, 24, [Lit("Where")], Body(13, color: "theme:accent", uppercase: true, spacing: 0.25)));
        s.Elements.Add(Text("venue", 30, y + 260, 330, 44, [Var("event.venue.name")], Heading(28)));
        s.Elements.Add(Text("address", 30, y + 306, 330, 44, [Var("event.venue.address")], Body(14)));
        s.Elements.Add(Link("map", 95, y + 360, 200, 44, "event.venue.mapLink", "Open the map"));
        s.Elements.Add(Dress("dress", 30, y + 440, 330, 120));
        s.Elements.Add(Rsvp("rsvp", 80, y + 600, 230, 58));
        foreach (var el in s.Elements.Where(e => e.Y >= y)) FadeUp(s, el, "rise");
        return s;
    }

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
        // Early enough that the fade (the first 15% of the track) is over by the time the element's
        // bottom reaches the bottom of the screen — the last thing on a page can't wait for more scroll.
        var start = Math.Max(0, Math.Min(el.Y - DesignCanvas.ReferenceViewport * 0.95, el.Y + el.H - DesignCanvas.ReferenceViewport - 210));
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
        if (preset.Origin is [var ox, var oy]) el.Origin = new DesignOrigin { X = ox, Y = oy };
        if (preset.ClipShape is not null) el.ClipShape = preset.ClipShape;
        if (preset.Split is { } split && el.Text is not null) el.Text.Split = new DesignSplit { By = split.By, Stagger = split.Stagger };
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
                RotateX = f.RotateX, RotateY = f.RotateY, SkewX = f.SkewX, Blur = f.Blur,
                Clip = f.Clip?.ToList(), Draw = f.Draw, Tracking = f.Tracking,
                Easing = f.Easing,
                Preset = slot,
            });
        }
        // Resting values made explicit at the preset's far end, so carry-forward can't leave an
        // enter's starting offset in place for the rest of the track.
        var tagged = el.Keyframes.Where(k => k.Preset == slot).ToList();
        var last = tagged.MaxBy(k => slot == "enter" ? k.T : -k.T);
        if (last is not null)
        {
            last.X ??= el.X; last.Y ??= el.Y; last.Rotate ??= el.Rotate; last.Scale ??= el.Scale; last.Opacity ??= el.Opacity;
            if (tagged.Any(k => k.RotateX is not null)) last.RotateX ??= 0;
            if (tagged.Any(k => k.RotateY is not null)) last.RotateY ??= 0;
            if (tagged.Any(k => k.SkewX is not null)) last.SkewX ??= 0;
            if (tagged.Any(k => k.Blur is not null)) last.Blur ??= 0;
            if (tagged.Any(k => k.Draw is not null)) last.Draw ??= 1;
            if (tagged.Any(k => k.Tracking is not null)) last.Tracking ??= 0;
            if (tagged.Any(k => k.Clip is not null) && el.ClipShape is { } kind) last.Clip ??= [.. DesignCompiler.FullClip(kind)];
        }
        el.Keyframes.Sort((a, b) => a.T.CompareTo(b.T));
        if (slot == "enter") el.Enter = preset.Id; else el.Exit = preset.Id;
    }

    /// <summary>A loop preset at a strength: offsets and turns scaled, scale and fade scaled about "no change".</summary>
    public static void ApplyLoop(DesignElement el, DesignCatalog.LoopPreset preset, double strength = 1, int? repeat = null)
    {
        strength = Math.Clamp(strength, 0, 3);
        if (preset.Origin is [var ox, var oy]) el.Origin = new DesignOrigin { X = ox, Y = oy };
        el.Loop = new DesignLoop
        {
            Preset = preset.Id, Strength = strength, Alternate = preset.Alternate,
            Repeat = Math.Clamp(repeat ?? preset.Repeat, 1, DesignCatalog.MaxLoopRepeat),
            Frames = preset.Frames.Select(f => new DesignLoopFrame
            {
                T = f.T,
                Dx = f.Dx == 0 ? null : Math.Round(f.Dx * strength, 3),
                Dy = f.Dy == 0 ? null : Math.Round(f.Dy * strength, 3),
                Rotate = f.Rotate == 0 ? null : Math.Round(f.Rotate * strength, 3),
                Scale = f.Scale == 1 ? null : Math.Round(1 + (f.Scale - 1) * strength, 4),
                Opacity = f.Opacity == 1 ? null : Math.Round(Math.Clamp(1 - (1 - f.Opacity) * strength, 0, 1), 4),
                Easing = f.Easing,
            }).ToList(),
        };
    }
}
