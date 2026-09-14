using Godot;

namespace EstunStudio;

/// <summary>Draws the enclosure at native UI resolution, without raster assets or canvas scaling.</summary>
public partial class PendantShell : Control
{
    public PendantShell()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready()
    {
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        if (Size.X < 40 || Size.Y < 80) return;
        var rect = new Rect2(Vector2.Zero, Size);

        // A narrow lip, the mould seam, and a broad satin top face make the enclosure
        // read as a manufactured object instead of another floating software card.
        var shadow = Round(new Color("#111519"), 23);
        shadow.ShadowColor = new Color(0, 0, 0, .32f);
        shadow.ShadowSize = 9;
        shadow.ShadowOffset = new Vector2(0, 5);
        DrawStyleBox(shadow, rect.Grow(-1));
        DrawStyleBox(Round(new Color("#50565b"), 22), rect.Grow(-2));
        DrawStyleBox(Round(new Color("#151a1e"), 21), rect.Grow(-3));
        DrawStyleBox(Round(new Color("#373d42"), 20), rect.Grow(-4));
        DrawStyleBox(Round(new Color("#252b30"), 18), new Rect2(6, 8, Size.X - 12, Size.Y - 14));

        // Warm elastomer edges are deliberately desaturated: safety red belongs to
        // the stop switch, and screen selection keeps the only strong UI accent.
        float gripTop = Mathf.Min(104, Size.Y * .17f);
        float gripHeight = Mathf.Max(20, Size.Y - gripTop - 51);
        DrawGrip(new Rect2(1, gripTop, 9, gripHeight), false);
        DrawGrip(new Rect2(Size.X - 10, gripTop, 9, gripHeight), true);

        // The top shoulder catches the room light; the lower shoulder rolls away.
        DrawLine(new Vector2(27, 5), new Vector2(Size.X - 27, 5), new Color(1, 1, 1, .12f), 1, true);
        DrawLine(new Vector2(28, Size.Y - 6), new Vector2(Size.X - 28, Size.Y - 6), new Color(0, 0, 0, .45f), 1, true);
        DrawLine(new Vector2(10, 34), new Vector2(10, gripTop - 5), new Color(1, 1, 1, .07f), 1, true);
        DrawLine(new Vector2(Size.X - 10, 34), new Vector2(Size.X - 10, gripTop - 5), new Color(0, 0, 0, .25f), 1, true);

        // Sparse, sub-contrast grain is limited to the physical border. Never put
        // a noise layer over the LCD, labels, or interactive controls.
        for (int y = 28; y < Size.Y - 27; y += 4)
        {
            float alpha = .018f + ((y * 19) % 7) * .002f;
            DrawRect(new Rect2(12 + (y % 3), y, 1, 1), new Color(1, 1, 1, alpha));
            DrawRect(new Rect2(Size.X - 15 + (y % 2), y + 1, 1, 1), new Color(1, 1, 1, alpha));
        }
        DrawScrew(new Vector2(15, 18));
        DrawScrew(new Vector2(Size.X - 15, 18));
        DrawScrew(new Vector2(15, Size.Y - 18));
        DrawScrew(new Vector2(Size.X - 15, Size.Y - 18));
    }

    private void DrawGrip(Rect2 rect, bool right)
    {
        DrawStyleBox(Round(new Color("#17191b"), 5), rect.Grow(1));
        DrawStyleBox(Round(new Color("#8d653a"), 4), rect);
        DrawStyleBox(Round(new Color("#b5864e"), 3), new Rect2(rect.Position + new Vector2(right ? 0 : 2, 1), new Vector2(6, rect.Size.Y - 3)));
        float edge = right ? rect.Position.X : rect.End.X - 1;
        DrawLine(new Vector2(edge, rect.Position.Y + 5), new Vector2(edge, rect.End.Y - 5), new Color("#cf9a58"), 1, true);
        // Short grip ribs, kept away from the straight centre seam.
        for (int i = 0; i < 7; i++)
        {
            float y = rect.End.Y - 22 - i * 8;
            if (y < rect.Position.Y + 15) break;
            DrawLine(new Vector2(rect.Position.X + 1, y), new Vector2(rect.End.X - 1, y), new Color(0, 0, 0, .29f), 2, true);
            DrawLine(new Vector2(rect.Position.X + 1, y + 2), new Vector2(rect.End.X - 1, y + 2), new Color(1, 1, 1, .09f), 1, true);
        }
    }

    private void DrawScrew(Vector2 center)
    {
        DrawCircle(center + new Vector2(0, 1), 4, new Color("#11161a"));
        DrawCircle(center, 3.1f, new Color("#40484d"));
        DrawArc(center, 2.5f, Mathf.Pi, Mathf.Tau, 14, new Color("#687076"), .7f, true);
        DrawLine(center + new Vector2(-1.35f, 0), center + new Vector2(1.35f, 0), new Color("#161b1f"), 1, true);
        DrawLine(center + new Vector2(0, -1.35f), center + new Vector2(0, 1.35f), new Color("#161b1f"), 1, true);
    }

    internal static StyleBoxFlat Round(Color color, int radius, Color? border = null)
    {
        return new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            BorderWidthLeft = border.HasValue ? 1 : 0, BorderWidthTop = border.HasValue ? 1 : 0,
            BorderWidthRight = border.HasValue ? 1 : 0, BorderWidthBottom = border.HasValue ? 1 : 0,
            BorderColor = border ?? Colors.Transparent,
            AntiAliasing = true, AntiAliasingSize = 1
        };
    }
}

/// <summary>A low-profile moulded key. Text remains Godot's native, unscaled Button text.</summary>
public partial class PhysicalKeyButton : Button
{
    private Color _accent = new("#b5c0c7");
    private bool _illuminated;
    public Color Accent
    {
        get => _accent;
        set { _accent = value; if (IsInsideTree()) ApplyStyles(); QueueRedraw(); }
    }
    public bool Illuminated
    {
        get => _illuminated;
        set { _illuminated = value; if (IsInsideTree()) ApplyStyles(); QueueRedraw(); }
    }

    public PhysicalKeyButton()
    {
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
    }

    public override void _Ready()
    {
        ApplyStyles();
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
        ButtonDown += QueueRedraw;
        ButtonUp += QueueRedraw;
        Toggled += _ => QueueRedraw();
        Resized += QueueRedraw;
    }

    private void ApplyStyles()
    {
        Color face = _illuminated ? new Color("#3b494b").Lerp(_accent, .13f) : new Color("#343d44");
        Color edge = _illuminated ? _accent.Darkened(.35f) : new Color("#59636b");
        AddThemeStyleboxOverride("normal", KeyStyle(face, edge, false));
        AddThemeStyleboxOverride("hover", KeyStyle(face.Lightened(.11f), edge.Lightened(.16f), false));
        AddThemeStyleboxOverride("pressed", KeyStyle(face.Darkened(.2f), _accent.Darkened(.25f), true));
        AddThemeStyleboxOverride("hover_pressed", KeyStyle(face.Darkened(.12f), _accent, true));
        AddThemeStyleboxOverride("disabled", KeyStyle(new Color("#282f34"), new Color("#3b454c"), false));
        AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        AddThemeColorOverride("font_color", new Color("#ecf0f2"));
        AddThemeColorOverride("font_hover_color", Colors.White);
        AddThemeColorOverride("font_pressed_color", Colors.White);
        AddThemeColorOverride("font_disabled_color", new Color("#64717b"));
    }

    private static StyleBoxFlat KeyStyle(Color face, Color edge, bool pressed)
    {
        var style = PendantShell.Round(face, 7, edge);
        style.BorderWidthBottom = pressed ? 1 : 3;
        style.BorderColor = edge;
        style.ShadowColor = new Color(0, 0, 0, pressed ? .16f : .45f);
        style.ShadowSize = pressed ? 1 : 2;
        style.ShadowOffset = new Vector2(0, pressed ? 0 : 2);
        style.ContentMarginLeft = style.ContentMarginRight = 7;
        style.ContentMarginTop = 5;
        style.ContentMarginBottom = 5;
        return style;
    }

    public override void _Draw()
    {
        if (Size.X < 18 || Size.Y < 18) return;
        bool pressed = IsPressed();
        float y = pressed ? 3 : 2;
        DrawLine(new Vector2(8, y), new Vector2(Size.X - 8, y), new Color(1, 1, 1, Disabled ? .025f : pressed ? .04f : .14f), 1, true);
        DrawLine(new Vector2(8, Size.Y - 2), new Vector2(Size.X - 8, Size.Y - 2), new Color(0, 0, 0, .30f), 1, true);
        if (_illuminated)
        {
            var center = new Vector2(Size.X - 8, 8);
            DrawCircle(center, 2.7f, new Color(0, 0, 0, .55f));
            DrawCircle(center, 1.6f, Disabled ? _accent.Darkened(.65f) : _accent);
            if (!Disabled) DrawCircle(center - new Vector2(.4f, .4f), .55f, Colors.White);
        }
    }
}

/// <summary>A red twist-release mushroom switch seated in a yellow safety collar.</summary>
public partial class MushroomStopButton : Button
{
    private bool _latched;
    public bool Latched
    {
        get => _latched;
        set { if (_latched == value) return; _latched = value; QueueRedraw(); }
    }

    public MushroomStopButton()
    {
        CustomMinimumSize = new Vector2(64, 64);
        FocusMode = FocusModeEnum.None;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        Text = "";
        Name = "EmergencyStop";
    }

    public override void _Ready()
    {
        foreach (string state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled", "focus" })
            AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        MouseEntered += QueueRedraw;
        MouseExited += QueueRedraw;
        ButtonDown += QueueRedraw;
        ButtonUp += QueueRedraw;
        Resized += QueueRedraw;
    }

    public override void _Draw()
    {
        float radius = Mathf.Min(Size.X, Size.Y) * .455f;
        Vector2 center = Size * .5f - new Vector2(0, 1);
        bool depressed = IsPressed() || _latched;
        float travel = depressed ? 2 : 0;

        Ellipse(center + new Vector2(1, 5), radius, .94f, new Color(0, 0, 0, .35f));
        Ellipse(center + new Vector2(0, 1), radius, .94f, new Color("#2b2616"));
        Ellipse(center, radius - 1.3f, .94f, new Color("#ac872d"));
        Ellipse(center - new Vector2(0, 1), radius - 2.2f, .94f, new Color("#e7bd49"));
        DrawArc(center, radius - 4, Mathf.Pi * 1.10f, Mathf.Pi * 1.9f, 40, new Color("#ffe58d"), .8f, true);

        float cap = radius * .76f;
        Vector2 top = center + new Vector2(0, -3 + travel);
        Ellipse(center + new Vector2(0, 3), cap + 1, .94f, new Color("#501b1c"));
        Ellipse(center + new Vector2(0, 1), cap, .95f, new Color("#781e25"));
        Ellipse(top + new Vector2(0, 1), cap, .95f, new Color("#a72a31"));
        Ellipse(top, cap - 1.5f, .95f, new Color(IsHovered() ? "#e04b4c" : "#ce3a40"));
        Ellipse(top - new Vector2(0, 1), cap - 3.5f, .95f, new Color(IsHovered() ? "#d33c41" : "#c43139"));
        DrawArc(top, cap - 2.5f, Mathf.Pi * 1.11f, Mathf.Pi * 1.86f, 32, new Color(1, .67f, .64f, .62f), 1, true);
        DrawArc(top + new Vector2(0, 1), cap - 1.5f, .17f, Mathf.Pi * .88f, 32, new Color(.35f, .025f, .04f, .55f), 1.5f, true);

        Font font = GetThemeFont("font");
        int fontSize = radius >= 33 ? 13 : 11;
        const string label = "STOP";
        Vector2 textSize = font.GetStringSize(label, HorizontalAlignment.Left, -1, fontSize);
        Vector2 baseline = top + new Vector2(-textSize.X * .5f, (font.GetAscent(fontSize) - font.GetDescent(fontSize)) * .5f);
        DrawString(font, baseline + new Vector2(0, 1), label, HorizontalAlignment.Left, -1, fontSize, new Color(0, 0, 0, .23f));
        DrawString(font, baseline, label, HorizontalAlignment.Left, -1, fontSize, new Color("#fff3e9"));

        // Tiny moulded rotation marks communicate a physical twist-release cap.
        Vector2 mark = top + new Vector2(0, -cap * .63f);
        DrawLine(mark + new Vector2(-4, 0), mark + new Vector2(4, 0), new Color(1, .86f, .82f, .58f), .8f, true);
        DrawLine(mark + new Vector2(4, 0), mark + new Vector2(2, -2), new Color(1, .86f, .82f, .58f), .8f, true);
    }

    private void Ellipse(Vector2 center, float radius, float aspect, Color color)
    {
        DrawSetTransform(center, 0, new Vector2(1, aspect));
        DrawCircle(Vector2.Zero, radius, color);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
