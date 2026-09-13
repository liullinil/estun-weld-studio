using Godot;
using System;
using System.Collections.Generic;

namespace EstunStudio;

/// <summary>Camera-oriented CAD view cube. Its 3 × 3 face regions cover all 26 standard views.</summary>
public partial class ViewCube : Control
{
    public event Action<Vector3>? ViewRequested;
    public event Action? HomeRequested;
    public event Action<Vector2>? OrbitRequested;
    public static Vector3 DefaultDirection => new Vector3(.68f, .48f, .76f).Normalized();

    private record Face(string Name, Vector3 Normal, Vector3 U, Vector3 V);
    private record Region(Vector2[] Polygon, Vector3 Direction, string Caption);
    private static readonly Face[] Faces =
    {
        new("FRONT", Vector3.Back, Vector3.Right, Vector3.Up),
        new("BACK", Vector3.Forward, Vector3.Left, Vector3.Up),
        new("RIGHT", Vector3.Right, Vector3.Forward, Vector3.Up),
        new("LEFT", Vector3.Left, Vector3.Back, Vector3.Up),
        new("TOP", Vector3.Up, Vector3.Right, Vector3.Forward),
        new("BOTTOM", Vector3.Down, Vector3.Right, Vector3.Back)
    };
    private readonly List<Region> _regions = new();
    private Basis _view = Basis.Identity;
    private Font _font = null!;
    private Vector2 _pointer = new(-100, -100), _pressPosition;
    private bool _pressed, _dragged, _built;
    private readonly Rect2 _home = new(116, 4, 24, 24);
    private static readonly Vector2 Center = new(73, 79);
    private const float ScaleFactor = 30;
    private static readonly Color Amber = new("f4b658");

    public void Build()
    {
        if (_built) return;
        _built = true;
        Name = "ViewCube";
        Size = CustomMinimumSize = new Vector2(146, 158);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        _font = GD.Load<Font>("res://Assets/Fonts/Inter-SemiBold.ttf") ?? ThemeDB.FallbackFont;
        TooltipText = "Click a face, edge or corner to orient the view. Drag to orbit. Home resets the camera.";
        MouseExited += () => { _pointer = new Vector2(-100, -100); QueueRedraw(); };
        QueueRedraw();
    }

    public void SetCameraBasis(Basis cameraBasis)
    {
        var next = cameraBasis.Orthonormalized().Inverse();
        if (_view.IsEqualApprox(next)) return;
        _view = next;
        QueueRedraw();
    }

    private Vector2 Project(Vector3 position)
    {
        var p = _view * position;
        float perspective = 5f / (5f - p.Z);
        return Center + new Vector2(p.X, -p.Y) * ScaleFactor * perspective;
    }

    private Vector2[] Quad(Face face, float x0, float y0, float x1, float y1) =>
        new[] { Project(face.Normal + face.U*x0 + face.V*y0), Project(face.Normal + face.U*x1 + face.V*y0),
            Project(face.Normal + face.U*x1 + face.V*y1), Project(face.Normal + face.U*x0 + face.V*y1) };

    private void Outline(Vector2[] polygon, Color color, float width = 1)
    {
        var loop = new Vector2[polygon.Length + 1];
        polygon.CopyTo(loop, 0); loop[^1] = polygon[0];
        DrawPolyline(loop, color, width, true);
    }

    public override void _Draw()
    {
        if (!_built) return;
        _regions.Clear();
        var panel = new StyleBoxFlat { BgColor = new Color(.045f,.067f,.084f,.83f), BorderColor = new Color(.44f,.52f,.58f,.22f),
            BorderWidthBottom = 1, BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1,
            CornerRadiusTopLeft = 12, CornerRadiusTopRight = 12, CornerRadiusBottomLeft = 12, CornerRadiusBottomRight = 12,
            ShadowColor = new Color(0,0,0,.17f), ShadowSize = 9 };
        DrawStyleBox(panel, new Rect2(Vector2.Zero, Size));
        DrawString(_font,new Vector2(13,20),"VIEW",HorizontalAlignment.Left,-1,9,new Color("98a8b3"));
        DrawArc(Center + new Vector2(0,8), 57, 0, Mathf.Tau, 72, new Color(.43f,.52f,.58f,.14f), 1, true);

        bool homeHover = _home.HasPoint(_pointer);
        var homeColor = homeHover ? Amber : new Color("9cabb5");
        if(homeHover) DrawStyleBox(new StyleBoxFlat { BgColor = new Color(.65f,.43f,.12f,.17f), CornerRadiusTopLeft=5,CornerRadiusTopRight=5,CornerRadiusBottomLeft=5,CornerRadiusBottomRight=5 },_home);
        DrawPolyline(new[] { new Vector2(121,16),new Vector2(128,10),new Vector2(135,16) },homeColor,1.4f,true);
        DrawPolyline(new[] { new Vector2(123,15),new Vector2(123,22),new Vector2(133,22),new Vector2(133,15) },homeColor,1.4f,true);

        var visible = new List<Face>();
        // The virtual eye is five cube units away; perspective-facing is n·(eye-face) > 0.
        foreach(var face in Faces) if((_view * face.Normal).Z > .2001f) visible.Add(face);
        visible.Sort((a,b) => (_view*a.Normal).Z.CompareTo((_view*b.Normal).Z));
        string caption = "FACE · EDGE · CORNER";
        foreach(var face in visible)
        {
            float facing = (_view * face.Normal).Z;
            var polygon = Quad(face,-1,-1,1,1);
            var shade = new Color("35434e").Lerp(new Color("71818b"),facing*.62f);
            DrawColoredPolygon(polygon,shade);
            for(int row=0;row<3;row++) for(int col=0;col<3;col++)
            {
                // Wide face centre and narrow edge strips match familiar CAD view cubes.
                float[] split = { -1,-.54f,.54f,1 };
                var regionPolygon = Quad(face,split[col],split[row],split[col+1],split[row+1]);
                Vector3 direction = (face.Normal + face.U*(col-1) + face.V*(row-1)).Normalized();
                string regionName = col==1 && row==1 ? face.Name : DirectionName(direction);
                _regions.Add(new Region(regionPolygon,direction,regionName));
                if(!_pressed && Geometry2D.IsPointInPolygon(_pointer,regionPolygon))
                {
                    DrawColoredPolygon(regionPolygon,new Color(Amber.R,Amber.G,Amber.B,.64f));
                    Outline(regionPolygon,new Color(Amber.R,Amber.G,Amber.B,.9f),1.25f);
                    caption=regionName;
                }
            }
            Outline(polygon,new Color("a0adb5"),1.3f);
            var labelCenter=Project(face.Normal*1.001f);
            var labelColor=new Color("f1f3f2");
            float labelWidth=_font.GetStringSize(face.Name,HorizontalAlignment.Left,-1,9).X;
            if(facing>.20f)
            {
                // Apply the projected face plane to the lettering so it rotates with the cube.
                Vector2 x=(Project(face.Normal + face.U*.3f)-labelCenter)/(.3f*ScaleFactor);
                Vector2 y=(Project(face.Normal - face.V*.3f)-labelCenter)/(.3f*ScaleFactor);
                DrawSetTransformMatrix(new Transform2D(x,y,labelCenter));
                DrawString(_font,new Vector2(-labelWidth*.5f,3),face.Name,HorizontalAlignment.Left,-1,9,labelColor);
                DrawSetTransformMatrix(Transform2D.Identity);
            }
        }
        if(homeHover) caption="HOME VIEW";
        DrawString(_font,new Vector2(0,145),caption,HorizontalAlignment.Center,146,8,new Color("9aaebc"));

        // The triad makes the scene's Y-up convention visible without occupying the viewport.
        var origin=new Vector2(21,120);
        foreach(var item in new[] { (Vector3.Right,"X",new Color("ed8778")),(Vector3.Up,"Y",new Color("93cead")),(Vector3.Back,"Z",new Color("81b3e6")) })
        {
            Vector3 p=_view*item.Item1;
            var end=origin+new Vector2(p.X,-p.Y)*12;
            DrawLine(origin,end,item.Item3,1.4f,true);
            if(Mathf.Abs(p.Z)<.95f) DrawString(_font,end+new Vector2(-2,-3),item.Item2,HorizontalAlignment.Left,-1,7,item.Item3);
        }
        DrawCircle(origin,2,new Color("bdc7cd"));
    }

    private static string DirectionName(Vector3 d)
    {
        var words=new List<string>();
        if(d.Y>.1f) words.Add("TOP"); else if(d.Y<-.1f) words.Add("BOTTOM");
        if(d.Z>.1f) words.Add("FRONT"); else if(d.Z<-.1f) words.Add("BACK");
        if(d.X>.1f) words.Add("RIGHT"); else if(d.X<-.1f) words.Add("LEFT");
        return string.Join(" · ",words);
    }

    public override void _GuiInput(InputEvent e)
    {
        if(e is InputEventMouseMotion motion)
        {
            _pointer=motion.Position;
            if(_pressed && (_dragged || motion.Position.DistanceTo(_pressPosition)>4))
            { _dragged=true; OrbitRequested?.Invoke(motion.Relative); }
            QueueRedraw(); AcceptEvent();
        }
        else if(e is InputEventMouseButton button && button.ButtonIndex==MouseButton.Left)
        {
            _pointer=button.Position;
            if(button.Pressed) { _pressed=true;_dragged=false;_pressPosition=button.Position; }
            else
            {
                if(_pressed && !_dragged)
                {
                    if(_home.HasPoint(button.Position)) HomeRequested?.Invoke();
                    else for(int i=_regions.Count-1;i>=0;i--)
                        if(Geometry2D.IsPointInPolygon(button.Position,_regions[i].Polygon))
                        { ViewRequested?.Invoke(_regions[i].Direction);break; }
                }
                _pressed=false;
            }
            QueueRedraw(); AcceptEvent();
        }
    }
}
