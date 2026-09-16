using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EstunStudio;

/// <summary>The same orthographic view cube and 4/7-pixel edge/corner targets as SwissCAM.</summary>
public partial class ViewCube : Control
{
    public event Action<Vector3>? ViewRequested;
    public event Action<Vector3>? PresetRequested;
    public event Action? HomeRequested;
    public event Action<Vector2>? OrbitRequested;
    public static Vector3 DefaultDirection=>new Vector3(-1,1,1).Normalized();
    private record Face(string Label,Vector3 Normal,int[] Vertices);
    private record Hit(string Key,Vector3 Direction,Vector2[] Polygon,Vector2 Center,float Facing,bool Face);
    private static readonly Vector3[] Vertices={new(-1,-1,-1),new(1,-1,-1),new(1,1,-1),new(-1,1,-1),new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)};
    private static readonly Face[] Faces={new("Front",Vector3.Back,new[]{4,5,6,7}),new("Back",Vector3.Forward,new[]{1,0,3,2}),new("Top",Vector3.Up,new[]{7,6,2,3}),new("Bottom",Vector3.Down,new[]{0,1,5,4}),new("Left",Vector3.Left,new[]{0,4,7,3}),new("Right",Vector3.Right,new[]{5,1,2,6})};
    private Basis _view=Basis.Identity;
    private readonly List<Hit> _faces=new(),_corners=new(),_edges=new();
    private Font _font=null!;private Hit? _hover;private Vector2 _press,_last;private bool _pressed,_dragged,_built;
    public void Build(){if(_built)return;_built=true;Name="ViewCube";Size=CustomMinimumSize=new Vector2(104,104);MouseFilter=MouseFilterEnum.Stop;FocusMode=FocusModeEnum.All;MouseDefaultCursorShape=CursorShape.PointingHand;_font=GD.Load<Font>("res://Assets/Fonts/Inter-Regular.ttf");TooltipText="Click a face, edge or corner. Drag to rotate. Home restores isometric.";MouseExited+=()=>{_hover=null;QueueRedraw();};Refresh();}
    public void SetCameraBasis(Basis basis){var next=basis.Orthonormalized().Inverse();if(_view.IsEqualApprox(next))return;_view=next;Refresh();}
    private void Refresh()
    {
        _faces.Clear();_corners.Clear();_edges.Clear();var points=Vertices.Select(v=>{var p=_view*v;return new Vector2(52+p.X*22.36f,52-p.Y*22.36f);}).ToArray();
        var seenCorners=new HashSet<int>();var seenEdges=new HashSet<string>();
        foreach(var face in Faces){float facing=(_view*face.Normal).Z;if(facing<=.012f)continue;var polygon=face.Vertices.Select(i=>points[i]).ToArray();Vector2 center=polygon.Aggregate(Vector2.Zero,(a,b)=>a+b)*.25f;_faces.Add(new(face.Label,face.Normal,polygon,center,facing,true));
            for(int i=0;i<4;i++){int a=face.Vertices[i],b=face.Vertices[(i+1)%4];if(seenCorners.Add(a))_corners.Add(new("corner"+a,Vertices[a],new[]{points[a]},points[a],0,false));string key=$"{Math.Min(a,b)}:{Math.Max(a,b)}";if(seenEdges.Add(key))_edges.Add(new(key,(Vertices[a]+Vertices[b])*.5f,new[]{points[a],points[b]},(points[a]+points[b])*.5f,0,false));}}
        _hover=null;QueueRedraw();
    }
    private Hit? Pick(Vector2 point)
    {
        Hit? result=null;float distance=7;
        foreach(var hit in _corners){float d=point.DistanceTo(hit.Center);if(d<distance){distance=d;result=hit;}}if(result!=null)return result;
        distance=4;foreach(var hit in _edges){Vector2 a=hit.Polygon[0],b=hit.Polygon[1],ab=b-a;float t=Mathf.Clamp((point-a).Dot(ab)/Mathf.Max(ab.LengthSquared(),1e-10f),0,1),d=point.DistanceTo(a+ab*t);if(d<distance){distance=d;result=hit;}}if(result!=null)return result;
        return _faces.FirstOrDefault(hit=>Geometry2D.IsPointInPolygon(point,hit.Polygon));
    }
    public override void _Draw()
    {
        if(!_built)return;
        foreach(var face in _faces){bool active=_hover?.Key==face.Key;Color fill=active?new Color("468d88"):new Color("3b5269").Lerp(new Color("72899e"),face.Facing*.65f);DrawColoredPolygon(face.Polygon,fill);DrawPolyline(face.Polygon.Append(face.Polygon[0]).ToArray(),new Color("8ba2b7"),1.2f,true);if(face.Facing<.23f)continue;var intersections=new List<float>();for(int i=0;i<4;i++){Vector2 a=face.Polygon[i],b=face.Polygon[(i+1)%4];if(Mathf.Abs(a.Y-b.Y)>.001f&&face.Center.Y>=Mathf.Min(a.Y,b.Y)&&face.Center.Y<=Mathf.Max(a.Y,b.Y))intersections.Add(a.X+(b.X-a.X)*(face.Center.Y-a.Y)/(b.Y-a.Y));}if(intersections.Count<2)continue;float width=intersections.Max()-intersections.Min()-4;int size=face.Facing>.8f?10:9;while(size>=7&&_font.GetStringSize(face.Key,HorizontalAlignment.Left,-1,size).X>width)size--;if(size>=7)DrawString(_font,face.Center+new Vector2(-width*.5f,size*.35f),face.Key,HorizontalAlignment.Center,width,size,new Color("f5f9fc"));}
        if(_hover is {Face:false} h){if(h.Polygon.Length==1){DrawCircle(h.Center,5,new Color("5ee0c0"));DrawArc(h.Center,5,0,Mathf.Tau,24,new Color("d2fff4"),1,true);}else DrawLine(h.Polygon[0],h.Polygon[1],new Color("5ee0c0"),4,true);}
    }
    public override void _GuiInput(InputEvent input)
    {
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left} button){if(button.Pressed){if(Pick(button.Position)==null)return;_pressed=true;_dragged=false;_press=_last=button.Position;GrabFocus();}else End(button.Position);AcceptEvent();}
        else if(input is InputEventMouseMotion motion&&!_pressed){_hover=Pick(motion.Position);QueueRedraw();AcceptEvent();}
        else if(input is InputEventKey {Pressed:true,Echo:false} key){Vector3? direction=key.Keycode switch{Key.F=>Vector3.Back,Key.B=>Vector3.Forward,Key.T=>Vector3.Up,Key.D=>Vector3.Down,Key.L=>Vector3.Left,Key.R=>Vector3.Right,_=>null};if(direction.HasValue)PresetRequested?.Invoke(direction.Value);else if(key.Keycode is Key.Home or Key.Enter)HomeRequested?.Invoke();else return;AcceptEvent();}
    }
    public override void _Input(InputEvent input)
    {
        if(!_pressed)return;
        if(input is InputEventMouseMotion){var point=GetLocalMousePosition();_dragged|=point.DistanceTo(_press)>4;if(_dragged){OrbitRequested?.Invoke((point-_last)*2.333333f);_hover=null;}_last=point;QueueRedraw();GetViewport().SetInputAsHandled();}
        else if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:false}){End(GetLocalMousePosition());GetViewport().SetInputAsHandled();}
    }
    public override void _Notification(int what){if(what==NotificationApplicationFocusOut)_pressed=false;}
    private void End(Vector2 point){if(_pressed&&!_dragged&&Pick(point) is {} hit){if(hit.Face)PresetRequested?.Invoke(hit.Direction.Normalized());else ViewRequested?.Invoke(hit.Direction.Normalized());}_pressed=false;_hover=null;QueueRedraw();}
}
