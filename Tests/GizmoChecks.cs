using Godot;
using System;

namespace EstunStudio;

/// <summary>Projection, axis constraints, rotation turns and real GUI event routing.</summary>
public partial class GizmoChecks : Node
{
    private int _checks;
    private static readonly Vector3[] Axes={Vector3.Right,Vector3.Up,Vector3.Back};
    public override async void _Ready()
    {
        try
        {
            var parent=new Node3D {Rotation=new Vector3(.17f,.33f,-.24f)};AddChild(parent);
            var target=new Node3D();parent.AddChild(target);target.GlobalPosition=new Vector3(.35f,.7f,-.22f);
            var camera=new Camera3D {Position=new Vector3(3,2.7f,4),Current=true,HOffset=.49f,Fov=40};AddChild(camera);camera.LookAt(target.GlobalPosition);
            var layer=new CanvasLayer();AddChild(layer);
            var ui=new Control {MouseFilter=Control.MouseFilterEnum.Ignore};layer.AddChild(ui);ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var gizmo=new TransformGizmo {Camera=camera,Target=target};ui.AddChild(gizmo);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            Transform3D initial=target.GlobalTransform;
            for(int axis=0;axis<3;axis++)
            {
                target.GlobalTransform=initial;
                float length=camera.GlobalPosition.DistanceTo(target.GlobalPosition)*.075f;
                Vector3 picked=target.GlobalPosition+Axes[axis]*length*.78f;
                Vector2 click=camera.UnprojectPosition(picked);
                Require(gizmo.Hit(click)==axis,$"World axis {axis} handle is correctly projected with camera HOffset");
                Require(gizmo.TryBeginDrag(click),$"Translation axis {axis} starts dragging");
                gizmo._Input(new InputEventMouseMotion {Position=camera.UnprojectPosition(picked+Axes[axis]*.45f)});
                Require(target.GlobalPosition.DistanceTo(initial.Origin+Axes[axis]*.45f)<.0001f,$"Rapid 450 mm drag remains precise on world axis {axis}, under rotated parent");
                Require(target.GlobalBasis.IsEqualApprox(initial.Basis),"Translation retains the workpiece rotation");
                gizmo._Input(new InputEventMouseButton {ButtonIndex=MouseButton.Left,Pressed=false});
                Require(!gizmo.Dragging,"Releasing pointer ends manipulation");
            }
            target.GlobalTransform=initial;
            ui.Position=new Vector2(43,19);ui.Scale=new Vector2(.86f,.93f);
            float radius=camera.GlobalPosition.DistanceTo(target.GlobalPosition)*.075f;
            Vector2 handle=camera.UnprojectPosition(target.GlobalPosition+Vector3.Right*radius*.8f);
            Require(gizmo.Hit(handle)==0,"Hit testing honors transformed canvas controls");
            ui.Position=Vector2.Zero;ui.Scale=Vector2.One;
            gizmo.RotationMode=true;
            for(int axis=0;axis<3;axis++)
            {
                target.GlobalTransform=initial;
                Vector3 u=Axes[(axis+1)%3],v=Axes[(axis+2)%3];
                float phase=.43f;
                Vector2 click=camera.UnprojectPosition(initial.Origin+(u*Mathf.Cos(phase)+v*Mathf.Sin(phase))*radius);
                Require(gizmo.Hit(click)==axis,$"Tilted rotation ring {axis} has a distinct hit target");
                Require(gizmo.TryBeginDrag(click),$"Rotation axis {axis} starts dragging");
                const int segments=150;float angle=Mathf.Tau*2+Mathf.Pi*.5f;
                for(int step=1;step<=segments;step++)
                {
                    float a=phase+angle*step/segments;
                    gizmo._Input(new InputEventMouseMotion {Position=camera.UnprojectPosition(initial.Origin+(u*Mathf.Cos(a)+v*Mathf.Sin(a))*radius)});
                }
                Require(BasisDistance(target.GlobalBasis,new Basis(Axes[axis],angle)*initial.Basis)<.0002f,$"Axis {axis}: 810° pointer path rotates accurately through full revolutions");
                Require(target.GlobalPosition.IsEqualApprox(initial.Origin),"Rotation retains the workpiece origin");
                gizmo._Input(new InputEventMouseButton {ButtonIndex=MouseButton.Left,Pressed=false});
            }
            target.GlobalTransform=initial;gizmo.RotationMode=false;
            handle=camera.UnprojectPosition(target.GlobalPosition+Vector3.Right*radius*.8f);
            Require(gizmo.TryBeginDrag(handle),"Begin drag before cancellation");
            gizmo._Input(new InputEventMouseMotion {Position=handle+new Vector2(35,0)});
            gizmo._Input(new InputEventKey {Keycode=Key.Escape,Pressed=true});
            Require(!gizmo.Dragging && target.GlobalTransform.IsEqualApprox(initial),"Escape restores complete transform and ends drag");
            Require(gizmo.TryBeginDrag(handle),"Begin drag before mode change");gizmo.RotationMode=true;
            Require(!gizmo.Dragging,"Changing manipulation mode releases the old constraint");
            gizmo.RotationMode=false;gizmo.Active=false;
            Require(gizmo.Hit(handle)==-1 && !gizmo.TryBeginDrag(handle),"Deactivated handles cannot be picked");gizmo.Active=true;
            ui.Hide();Require(!gizmo.TryBeginDrag(handle),"Hidden interface cannot manipulate CAD");ui.Show();
            Require(gizmo.TryBeginDrag(handle),"Begin drag before application focus loss");gizmo._Notification((int)NotificationApplicationFocusOut);
            Require(!gizmo.Dragging,"Focus loss releases the pointer constraint");

            // Deliver real viewport input: Stop panels and their Pass descendants must own their clicks.
            var blocker=new Panel {Position=handle-new Vector2(30,30),Size=new Vector2(60,60),MouseFilter=Control.MouseFilterEnum.Stop};ui.AddChild(blocker);
            var passChild=new Control {Size=new Vector2(60,60),MouseFilter=Control.MouseFilterEnum.Pass};blocker.AddChild(passChild);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            GetViewport().PushInput(new InputEventMouseMotion {Position=handle},true);
            GetViewport().PushInput(new InputEventMouseButton {Position=handle,ButtonIndex=MouseButton.Left,Pressed=true},true);
            Require(!gizmo.Dragging,"GUI Stop ancestor blocks gizmo dragging through Pass child");
            GetViewport().PushInput(new InputEventMouseButton {Position=handle,ButtonIndex=MouseButton.Left,Pressed=false},true);
            blocker.Hide();
            GetViewport().PushInput(new InputEventMouseMotion {Position=handle},true);
            GetViewport().PushInput(new InputEventMouseButton {Position=handle,ButtonIndex=MouseButton.Left,Pressed=true},true);
            Require(gizmo.Dragging,"Unobstructed viewport press reaches gizmo after GUI dispatch");
            GetViewport().PushInput(new InputEventMouseButton {Position=handle,ButtonIndex=MouseButton.Left,Pressed=false},true);
            Require(!gizmo.Dragging,"Viewport release ends active drag");
            target.GlobalTransform=initial;camera.Projection=Camera3D.ProjectionType.Orthogonal;camera.Size=3;
            radius=camera.GlobalPosition.DistanceTo(target.GlobalPosition)*.075f;
            Vector3 orthographicPick=initial.Origin+Vector3.Up*radius*.8f;
            Require(gizmo.TryBeginDrag(camera.UnprojectPosition(orthographicPick)),"Orthographic view supports translation handles");
            gizmo._Input(new InputEventMouseMotion {Position=camera.UnprojectPosition(orthographicPick+Vector3.Up*.31f)});
            Require(target.GlobalPosition.DistanceTo(initial.Origin+Vector3.Up*.31f)<.0001f,"Orthographic ray origins produce precise translation");
            gizmo._Input(new InputEventMouseButton {ButtonIndex=MouseButton.Left,Pressed=false});
            target.GlobalTransform=initial;camera.Projection=Camera3D.ProjectionType.Perspective;
            camera.HOffset=0;camera.GlobalPosition=initial.Origin+new Vector3(0,0,4);camera.LookAt(initial.Origin);
            gizmo.RotationMode=true;radius=.3f;
            float edgePhase=Mathf.Pi*.25f;
            Vector3 edgeRadial=Vector3.Up*Mathf.Cos(edgePhase)+Vector3.Back*Mathf.Sin(edgePhase);
            Vector3 edgeTangent=-Vector3.Up*Mathf.Sin(edgePhase)+Vector3.Back*Mathf.Cos(edgePhase);
            Vector2 edgeClick=camera.UnprojectPosition(initial.Origin+edgeRadial*radius);
            Require(gizmo.Hit(edgeClick)==0 && gizmo.TryBeginDrag(edgeClick),"Edge-on X rotation ring can be selected without unstable plane intersection");
            Vector2 tangentPixels=(camera.UnprojectPosition(initial.Origin+(edgeRadial+edgeTangent*.05f)*radius)-edgeClick)/.05f;
            gizmo._Input(new InputEventMouseMotion {Position=edgeClick+tangentPixels*.55f});
            Require(BasisDistance(target.GlobalBasis,initial.Basis)>.2f && float.IsFinite(target.GlobalBasis.Determinant()),"Edge-on tangent dragging rotates the part with finite geometry");
            gizmo._Input(new InputEventMouseButton {ButtonIndex=MouseButton.Left,Pressed=false});
            gizmo.RotationMode=false;
            Require(gizmo.Hit(camera.UnprojectPosition(initial.Origin+Vector3.Back*radius))==-1,"Axis pointing straight into camera has no misleading drag target");
            target.GlobalPosition=camera.GlobalPosition+Vector3.Back;
            Require(gizmo.Hit(edgeClick)==-1 && !gizmo.TryBeginDrag(edgeClick),"Geometry behind the camera cannot capture input");
            GD.Print($"PASS: {_checks} CAD gizmo checks");GetTree().Quit(0);
        }
        catch(Exception ex){GD.PrintErr($"FAIL: {ex}");GetTree().Quit(1);}
    }
    private static float BasisDistance(Basis a,Basis b)=>a.X.DistanceTo(b.X)+a.Y.DistanceTo(b.Y)+a.Z.DistanceTo(b.Z);
    private void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);_checks++;}
}
