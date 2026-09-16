using Godot;
using System;

namespace EstunStudio;

/// <summary>Zero at the left. A pressed handle captures input until release anywhere.</summary>
public partial class AngleRangeControl : Control
{
    public event Action<float, float>? RangeChanged;
    public float Minimum { get; private set; } = 85;
    public float Maximum { get; private set; } = 95;
    public bool IsDragging => _drag >= 0;
    private int _drag = -1;
    private Vector2 Centre => new(Size.X * .5f, Size.Y - 20);
    private float Radius => Mathf.Min(Size.X * .39f, Size.Y - 30);
    private Vector2 Point(float angle, float radius) => Centre + new Vector2(-Mathf.Cos(Mathf.DegToRad(angle)), -Mathf.Sin(Mathf.DegToRad(angle))) * radius;
    public override void _Ready() { CustomMinimumSize = new Vector2(0, 130); MouseFilter = MouseFilterEnum.Stop; }
    public void SetRange(float minimum, float maximum) { Minimum = Mathf.Clamp(minimum, 0, 180); Maximum = Mathf.Clamp(maximum, Minimum, 180); QueueRedraw(); }
    public override void _Draw()
    {
        float r = Radius; var muted = new Color("657987");
        DrawArc(Centre, r, Mathf.Pi, Mathf.Tau, 90, muted, 1, true);
        for (int angle = 0; angle <= 180; angle += 15) DrawLine(Point(angle, r - (angle % 45 == 0 ? 8 : 4)), Point(angle, r), muted, 1, true);
        DrawLine(Centre, Centre + Vector2.Right * r * .65f, new Color("ebf3f7"), 3, true);
        DrawLine(Centre, Centre + Vector2.Up * r * .65f, new Color("ebf3f7"), 3, true);
        DrawPolyline(new[] { Centre + new Vector2(0, -13), Centre + new Vector2(13, -13), Centre + new Vector2(13, 0) }, new Color("ebf3f7"), 1, true);
        var fill = new Vector2[34]; fill[0] = Centre;
        for (int i = 0; i <= 32; i++) fill[i + 1] = Point(Mathf.Lerp(Minimum, Maximum, i / 32f), r);
        if (Maximum - Minimum > .001f) DrawColoredPolygon(fill, new Color(.23f, .85f, .84f, .17f));
        foreach (var handle in new[] { (Angle: Minimum, Radius: r * .8f), (Angle: Maximum, Radius: r) }) { var p = Point(handle.Angle, handle.Radius); DrawLine(Centre, p, new Color("54e5df"), 2, true); DrawCircle(p, 7, new Color("54e5df")); DrawCircle(p, 3, new Color("183536")); }
        var font = ThemeDB.FallbackFont;
        DrawString(font, Point(0, r) + new Vector2(-5, 17), "0°", HorizontalAlignment.Left, -1, 11, muted);
        DrawString(font, Point(90, r) + new Vector2(-10, -10), "90°", HorizontalAlignment.Left, -1, 11, muted);
        DrawString(font, Point(180, r) + new Vector2(-12, 17), "180°", HorizontalAlignment.Left, -1, 11, muted);
    }
    public override void _GuiInput(InputEvent input)
    {
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            float first = click.Position.DistanceTo(Point(Minimum, Radius * .8f)), second = click.Position.DistanceTo(Point(Maximum, Radius));
            if (Math.Min(first, second) <= 13) { _drag = first <= second ? 0 : 1; AcceptEvent(); }
        }
    }
    public override void _Input(InputEvent input)
    {
        if (_drag < 0) return;
        if (input is InputEventMouseMotion motion) { MoveHandle(GetGlobalTransformWithCanvas().AffineInverse() * motion.Position); GetViewport().SetInputAsHandled(); }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false }) { _drag = -1; GetViewport().SetInputAsHandled(); }
    }
    private void MoveHandle(Vector2 mouse)
    {
        Vector2 d = mouse - Centre;
        float angle = d.Y > 0 ? (d.X > 0 ? 180 : 0) : Mathf.Clamp(Mathf.RadToDeg(Mathf.Atan2(-d.Y, -d.X)), 0, 180);
        if (_drag == 0) Minimum = Math.Min(angle, Maximum); else Maximum = Math.Max(angle, Minimum);
        QueueRedraw(); RangeChanged?.Invoke(Minimum, Maximum);
    }
    public override void _Notification(int what) { if (what == NotificationApplicationFocusOut) _drag = -1; }
}
