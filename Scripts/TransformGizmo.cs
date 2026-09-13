using Godot;
using System;

namespace EstunStudio;

/// <summary>World-axis CAD translation and rotation, with projection-correct pointer constraints.</summary>
public partial class TransformGizmo : Control
{
    public event Action? TransformChanged;
    private Node3D? _target;
    private Camera3D? _camera;
    private bool _rotationMode,_active=true;
    public Node3D? Target {get=>_target;set{if(_target==value)return;EndDrag();_target=value;}}
    public Camera3D? Camera {get=>_camera;set{if(_camera==value)return;EndDrag();_camera=value;}}
    public bool RotationMode {get=>_rotationMode;set{if(_rotationMode==value)return;EndDrag();_rotationMode=value;}}
    public bool Active {get=>_active;set{_active=value;if(!value)EndDrag();}}
    public bool Dragging=>_drag>=0;
    private int _drag=-1,_hover=-1;
    private Vector2 _last,_tangent;
    private Vector3 _origin,_previousRadial;
    private Transform3D _startTransform;
    private float _axisStart,_angle,_radius;
    private bool _edgeOnRotation;
    private readonly Color[] _colors={new("ed6e68"),new("89cf98"),new("72b2f2")};
    private static readonly Vector3[] Axes={Vector3.Right,Vector3.Up,Vector3.Back};
    private const int RingSegments=96;
    private bool CanInteract=>Active && IsVisibleInTree() && IsInstanceValid(Target) && IsInstanceValid(Camera) && Target!.IsInsideTree() && Camera!.IsInsideTree() && !Camera.IsPositionBehind(Target.GlobalPosition);
    private float Length=>Mathf.Max(.001f,Camera!.GlobalPosition.DistanceTo(Target!.GlobalPosition)*.075f);
    private Vector2 Project(Vector3 point)=>GetGlobalTransformWithCanvas().AffineInverse()*Camera!.UnprojectPosition(point);
    private Vector2 PointerLocal(Vector2 point)=>GetGlobalTransformWithCanvas().AffineInverse()*point;

    public override void _Ready(){MouseFilter=MouseFilterEnum.Ignore;SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);}
    public override void _Process(double delta)
    {
        Visible=Active && IsInstanceValid(Target) && IsInstanceValid(Camera);
        if(!CanInteract)EndDrag();
        QueueRedraw();
    }

    public override void _Draw()
    {
        if(!CanInteract)return;
        Vector3 origin=Target!.GlobalPosition;float length=Length;Vector2 center=Project(origin);
        for(int axis=0;axis<3;axis++)
        {
            Color color=_drag==axis||_hover==axis?new Color("ffd381"):_colors[axis];
            if(RotationMode)
            {
                Vector3 u=Axes[(axis+1)%3],v=Axes[(axis+2)%3];
                for(int segment=0;segment<RingSegments;segment++)
                {
                    Vector3 p=origin+(u*Mathf.Cos(segment*Mathf.Tau/RingSegments)+v*Mathf.Sin(segment*Mathf.Tau/RingSegments))*length;
                    Vector3 q=origin+(u*Mathf.Cos((segment+1)*Mathf.Tau/RingSegments)+v*Mathf.Sin((segment+1)*Mathf.Tau/RingSegments))*length;
                    if(Camera!.IsPositionBehind(p)||Camera.IsPositionBehind(q))continue;
                    float depth=(p-origin).Dot(Camera.GetCameraTransform().Basis.Z);
                    Color segmentColor=depth>=0?color:new Color(color.R,color.G,color.B,.38f);
                    DrawLine(Project(p),Project(q),segmentColor,axis==_hover||axis==_drag?3.8f:2.2f,true);
                }
            }
            else
            {
                Vector3 end3=origin+Axes[axis]*length;if(Camera!.IsPositionBehind(end3))continue;
                Vector2 end=Project(end3),delta=end-center;
                if(delta.Length()<15)continue;
                Vector2 direction=delta.Normalized(),normal=new(-direction.Y,direction.X);
                DrawLine(center,end,color,3.2f,true);
                DrawColoredPolygon(new[]{end,end-direction*12+normal*5,end-direction*12-normal*5},color);
                DrawString(ThemeDB.FallbackFont,end+new Vector2(8,-4),axis==0?"X":axis==1?"Y":"Z",HorizontalAlignment.Left,-1,12,color);
            }
        }
        DrawCircle(center,4,new Color("e4eef3"));
        if(Dragging)
        {
            string value=RotationMode?$"{Mathf.RadToDeg(_angle):0.0}°":$"{(Target.GlobalPosition-_origin).Dot(Axes[_drag])*1000:0.0} mm";
            DrawString(ThemeDB.FallbackFont,center+new Vector2(14,22),value,HorizontalAlignment.Left,-1,12,new Color("ffe1a2"));
        }
    }

    /// <summary>Called after GUI dispatch, before seam picking. Position is in viewport coordinates.</summary>
    public bool TryBeginDrag(Vector2 viewportPoint)
    {
        if(!CanInteract || Dragging)return Dragging;
        int axis=Hit(viewportPoint);if(axis<0)return false;
        _origin=Target!.GlobalPosition;_startTransform=Target.GlobalTransform;_radius=Length;
        _last=viewportPoint;_angle=0;
        if(RotationMode)
        {
            Vector3 eye=Camera!.GetCameraTransform().Origin;
            _edgeOnRotation=Mathf.Abs(Axes[axis].Dot((eye-_origin).Normalized()))<.12f;
            if(!_edgeOnRotation && !TryRadial(viewportPoint,axis,out _previousRadial))return false;
            if(_edgeOnRotation)
            {
                // An edge-on ring has no unique ray/plane intersection. Drag along its visible tangent.
                float phase=NearestRingPhase(viewportPoint,axis);
                Vector3 u=Axes[(axis+1)%3],v=Axes[(axis+2)%3];
                Vector3 radial=u*Mathf.Cos(phase)+v*Mathf.Sin(phase);
                Vector3 tangent=-u*Mathf.Sin(phase)+v*Mathf.Cos(phase);
                Vector2 p=Camera.UnprojectPosition(_origin+radial*_radius);
                _tangent=(Camera.UnprojectPosition(_origin+(radial+tangent*.05f)*_radius)-p)/.05f;
                if(_tangent.LengthSquared()<25)return false;
            }
        }
        else if(!TryAxisParameter(viewportPoint,axis,out _axisStart))return false;
        _drag=_hover=axis;QueueRedraw();return true;
    }

    public override void _UnhandledInput(InputEvent input)
    {
        if(!CanInteract)return;
        if(input is InputEventMouseMotion motion && !Dragging){_hover=Hit(motion.Position);QueueRedraw();}
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:true} click && TryBeginDrag(click.Position))GetViewport().SetInputAsHandled();
    }

    public override void _Input(InputEvent input)
    {
        if(!Dragging)return;
        if(!CanInteract){EndDrag();return;}
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:false})
        {EndDrag();GetViewport().SetInputAsHandled();return;}
        if(input is InputEventKey {Keycode:Key.Escape,Pressed:true})
        {
            Target!.GlobalTransform=_startTransform;EndDrag();TransformChanged?.Invoke();
            return; // Preserve the pendant's ESC emergency-stop shortcut.
        }
        if(input is not InputEventMouseMotion move)return;
        bool changed=false;
        if(RotationMode)
        {
            if(_edgeOnRotation)
            { _angle+=(move.Position-_last).Dot(_tangent)/_tangent.LengthSquared();changed=true; }
            else if(TryRadial(move.Position,_drag,out Vector3 radial))
            {
                // Incremental signed angles retain arbitrary complete revolutions without wrap jumps.
                _angle+=Mathf.Atan2(Axes[_drag].Dot(_previousRadial.Cross(radial)),_previousRadial.Dot(radial));
                _previousRadial=radial;changed=true;
            }
            if(changed)Target!.GlobalBasis=new Basis(Axes[_drag],_angle)*_startTransform.Basis;
        }
        else if(TryAxisParameter(move.Position,_drag,out float parameter))
        {
            Vector3 next=_origin+Axes[_drag]*(parameter-_axisStart);
            if(next.IsFinite()){Target!.GlobalPosition=next;changed=true;}
        }
        _last=move.Position;
        if(changed)TransformChanged?.Invoke();
        GetViewport().SetInputAsHandled();QueueRedraw();
    }

    private bool TryAxisParameter(Vector2 point,int axis,out float parameter)
    {
        Vector3 ray=Camera!.ProjectRayNormal(point),offset=Camera.ProjectRayOrigin(point)-_origin;
        float dot=Axes[axis].Dot(ray),denominator=1-dot*dot;
        parameter=0;if(denominator<.001f)return false;
        parameter=(Axes[axis].Dot(offset)-dot*ray.Dot(offset))/denominator;
        return float.IsFinite(parameter);
    }

    private bool TryRadial(Vector2 point,int axis,out Vector3 radial)
    {
        Vector3 ray=Camera!.ProjectRayNormal(point),eye=Camera.ProjectRayOrigin(point);
        float denominator=Axes[axis].Dot(ray);radial=Vector3.Zero;
        if(Mathf.Abs(denominator)<.0001f)return false;
        float distance=Axes[axis].Dot(_origin-eye)/denominator;if(distance<0)return false;
        radial=eye+ray*distance-_origin;
        if(!radial.IsFinite() || radial.LengthSquared()<.0000001f)return false;
        radial=radial.Normalized();return true;
    }

    private float NearestRingPhase(Vector2 point,int axis)
    {
        Vector2 local=PointerLocal(point);float best=float.PositiveInfinity,phase=0;
        Vector3 u=Axes[(axis+1)%3],v=Axes[(axis+2)%3];
        for(int i=0;i<RingSegments;i++)
        {
            float angle=i*Mathf.Tau/RingSegments;
            float distance=local.DistanceSquaredTo(Project(_origin+(u*Mathf.Cos(angle)+v*Mathf.Sin(angle))*_radius));
            if(distance<best){best=distance;phase=angle;}
        }
        return phase;
    }

    /// <summary>Return world X/Y/Z handle index, or -1. Useful to prioritize gizmos over CAD picking.</summary>
    public int Hit(Vector2 viewportPoint)
    {
        if(!CanInteract)return -1;
        Vector2 mouse=PointerLocal(viewportPoint);
        Vector3 origin=Target!.GlobalPosition;Vector2 center=Project(origin);float length=Length,best=9;int hit=-1;
        for(int axis=0;axis<3;axis++)
        {
            if(RotationMode)
            {
                Vector3 u=Axes[(axis+1)%3],v=Axes[(axis+2)%3];
                for(int segment=0;segment<RingSegments;segment++)
                {
                    Vector3 p=origin+(u*Mathf.Cos(segment*Mathf.Tau/RingSegments)+v*Mathf.Sin(segment*Mathf.Tau/RingSegments))*length;
                    Vector3 q=origin+(u*Mathf.Cos((segment+1)*Mathf.Tau/RingSegments)+v*Mathf.Sin((segment+1)*Mathf.Tau/RingSegments))*length;
                    if(Camera!.IsPositionBehind(p)||Camera.IsPositionBehind(q))continue;
                    float distance=SegmentDistance(mouse,Project(p),Project(q));
                    if(distance<best){best=distance;hit=axis;}
                }
            }
            else
            {
                Vector3 end3=origin+Axes[axis]*length;if(Camera!.IsPositionBehind(end3))continue;
                Vector2 end=Project(end3);if(end.DistanceSquaredTo(center)<225)continue;
                float distance=SegmentDistance(mouse,center.Lerp(end,.24f),end);
                if(distance<best){best=distance;hit=axis;}
            }
        }
        return hit;
    }

    private void EndDrag(){_drag=-1;_hover=-1;QueueRedraw();}
    public static float SegmentDistance(Vector2 point,Vector2 a,Vector2 b)
    {Vector2 d=b-a;return point.DistanceTo(a+d*Mathf.Clamp((point-a).Dot(d)/Mathf.Max(d.LengthSquared(),.0001f),0,1));}
    public override void _Notification(int what)
    {if(what==NotificationApplicationFocusOut || what==NotificationExitTree)EndDrag();}
}
