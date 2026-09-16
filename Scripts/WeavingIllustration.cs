using Godot;
namespace EstunStudio;

public partial class WeavingIllustration : Control
{
    public WeavingOptions Settings { get; set; } = new();
    public override void _Ready() { CustomMinimumSize = new Vector2(300, 145); MouseFilter = MouseFilterEnum.Ignore; }
    public override void _Draw()
    {
        var c = new Color("54e5df"); var muted = new Color("718692"); float w = Size.X - 32;
        var points = new Vector2[241]; float a = Mathf.Clamp(Settings.AmplitudeMm * 6, 5, 35), fade = Mathf.Clamp(Settings.FadeMm / 30, .025f, .45f);
        DrawLine(new(16, 63), new(Size.X - 16, 63), muted, 1, true);
        for (int i = 0; i < points.Length; i++) { float t = i / 240f; float envelope = Mathf.SmoothStep(0, 1, Mathf.Min(t, 1 - t) / fade); points[i] = new Vector2(16 + w * t, 63 - a * envelope * Mathf.Sin(t * Mathf.Tau * Settings.FrequencyHz * 2)); }
        DrawPolyline(points, c, 2, true);
        DrawLine(new(18, 63 - a), new(18, 63 + a), c, 1, true);
        var font = ThemeDB.FallbackFont;
        DrawString(font, new(24, 20), $"Amplitude  ±{Settings.AmplitudeMm:0.0} mm     Frequency  {Settings.FrequencyHz:0.0} Hz", HorizontalAlignment.Left, -1, 12, c);
        DrawString(font, new(18, 119), $"Smooth ends  {Settings.FadeMm:0.0} mm", HorizontalAlignment.Left, -1, 12, muted);
        Vector2 centre = new(Size.X - 62, 117); float angle = Mathf.DegToRad(Settings.AngleDegrees);
        DrawCircle(centre, 20, new Color(.22f, .35f, .39f, .4f)); DrawLine(centre, centre + new Vector2(Mathf.Cos(angle), -Mathf.Sin(angle)) * 20, c, 2, true);
        DrawString(font, centre + new Vector2(-15, 35), $"{Settings.AngleDegrees:0}°", HorizontalAlignment.Left, -1, 11, c);
    }
}
