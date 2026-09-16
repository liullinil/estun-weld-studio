using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>Behavioral native parity checks for the shipping controller and responsive desktop shell.</summary>
public partial class DesktopShellChecks : Node
{
    private int _checks;
    public override async void _Ready()
    {
        int code=0;SubViewport? viewport=null;
        try
        {
            viewport=new SubViewport{Size=new Vector2I(1440,900),OwnWorld3D=true};AddChild(viewport);
            var main=GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>();viewport.AddChild(main);await Settle();
            var nodes=Walk(main).ToArray();var controller=nodes.OfType<RobotController>().Single();controller.SetPhysicsProcess(false);
            var pendant=nodes.OfType<PendantPanel>().Single();var camera=nodes.OfType<Camera3D>().Single();var world=nodes.OfType<StudioWorld>().Single();
            Require(controller.DrivesEnabled&&!controller.AutomaticMode,"Drives are ready automatically in MANUAL");
            Require(!Walk(pendant).OfType<Button>().Any(b=>b.Name.ToString() is "Drives" or "Record" or "RUN" or "RESET"),"Obsolete drive and teach-program controls are absent");
            Require(Walk(pendant).OfType<Button>().Single(b=>b.Name=="GoHome").IsInsideTree(),"Go Home is available inside the pendant");
            Require(!Walk(pendant).OfType<ScrollContainer>().Any()&&!nodes.OfType<ScrollContainer>().Any(s=>s.Name=="PendantDock"),"Pendant has no scrolling container");
            Require(nodes.OfType<RoundIconButton>().Count()>=18,"Viewport display controls are individual round icons");
            Require(new RenderSettings().Msaa==3&&new RenderSettings().Ssil&&new RenderSettings().ResolutionMode==RenderResolution.Native,"Fresh native installations use sharp maximum quality");
            controller.SetAutomaticMode(true);Require(!controller.SetJointJog(0,1)&&!controller.SetTcpJog(2,1,true)&&!controller.GoHome(),"AUTO rejects joint jog, TOOL jog and Go Home");
            controller.SpeedOverride=.73f;Require(Mathf.IsEqualApprox(controller.SpeedOverride,.73f),"AUTO accepts speed changes");
            var pose=controller.AnglesDegrees;Require(controller.ApplyPlannedPose(pose,"AUTO fixture"),"AUTO accepts planned playback poses");controller.SetAutomaticMode(false);
            var stop=Walk(pendant).OfType<Button>().Single(b=>b.Name=="EmergencyStop");stop.EmitSignal(BaseButton.SignalName.Pressed);Require(controller.EmergencyStopped&&!controller.CanMove,"Physical STOP latches and halts motion");stop.EmitSignal(BaseButton.SignalName.Pressed);Require(!controller.EmergencyStopped&&controller.CanMove,"Second physical STOP press releases with drives ready");
            var original=controller.TcpTransform;world.UpdateTcp(original,false);Require(world.TcpAxes.GlobalBasis.Z.IsEqualApprox(-original.Basis.Y)&&world.TcpAxes.GlobalBasis.Y.IsEqualApprox(original.Basis.Z),"Displayed TCP Z points along the torch and Y matches the corrected tool frame");
            controller.SpeedOverride=.2f;Require(controller.SetTcpJog(2,1,true),"Positive TOOL Z jog starts");controller._PhysicsProcess(.02);controller.StopJog();Vector3 delta=controller.TcpPosition-original.Origin;Require(delta.Dot(-original.Basis.Y)>.00005f&&delta.Dot(original.Basis.Y)<0,"Positive TOOL Z moves outward along the torch");
            foreach(var size in new[]{new Vector2I(1180,700),new Vector2I(1440,900),new Vector2I(1920,1080)})
            {viewport.Size=size;await Settle();var dock=nodes.OfType<Control>().Single(c=>c.Name=="PendantDock");var bounds=new Rect2(Vector2.Zero,size);Require(Contains(bounds,dock.GetGlobalRect())&&Contains(bounds,pendant.GetGlobalRect()),$"Pendant fits {size.X} × {size.Y} without scroll");}
            nodes.OfType<WeldingWorkspace>().Single().SetPanelVisible(false);main.SetPendantVisible(false);
            for(int i=0;i<45;i++)main._UnhandledInput(new InputEventMouseButton{ButtonIndex=MouseButton.WheelUp,Pressed=true,Position=new Vector2(900,420)});
            Require(camera.Near<.001f&&camera.Position.IsFinite(),"Zoom passes prior minimum with adaptive near clipping");
            for(int i=0;i<100;i++)main._UnhandledInput(new InputEventMouseButton{ButtonIndex=MouseButton.WheelDown,Pressed=true,Position=new Vector2(900,420)});
            Require(camera.Far>50&&camera.Position.IsFinite(),"Zoom passes previous maximum with adaptive far clipping");
            GD.Print($"PASS: {_checks} desktop shell, controller and tool-frame parity checks");
        }
        catch(Exception error){code=1;GD.PrintErr("FAIL: "+error);}
        finally{viewport?.Free();}
        GetTree().Quit(code);
    }
    private void Require(bool condition,string text){if(!condition)throw new InvalidOperationException(text);_checks++;}
    private static bool Contains(Rect2 outer,Rect2 inner)=>inner.Position.X>=outer.Position.X-1&&inner.Position.Y>=outer.Position.Y-1&&inner.End.X<=outer.End.X+1&&inner.End.Y<=outer.End.Y+1;
    private async Task Settle(){for(int i=0;i<6;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
    private static IEnumerable<Node> Walk(Node node){foreach(Node child in node.GetChildren()){yield return child;foreach(Node nested in Walk(child))yield return nested;}}
}
