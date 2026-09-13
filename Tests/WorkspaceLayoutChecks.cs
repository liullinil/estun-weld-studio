using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>
/// Resize the shipping workspace at native pixels, including both independently
/// hideable docks. Geometry checks catch clipping/overlap without a desktop GPU.
/// </summary>
public partial class WorkspaceLayoutChecks : Node
{
    private int _checks;

    public override async void _Ready()
    {
        SubViewport? viewport=null;
        int exitCode=0;
        try
        {
            viewport=new SubViewport {Size=new Vector2I(1440,900),OwnWorld3D=true};AddChild(viewport);
            var main=GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>();viewport.AddChild(main);
            await Settle();
            var nodes=Descendants(main).ToArray();
            var ui=nodes.OfType<Control>().Single(c=>c.Name=="Workspace");
            var welding=nodes.OfType<WeldingWorkspace>().Single();
            var pendant=nodes.OfType<PendantPanel>().Single();
            var dock=nodes.OfType<ScrollContainer>().Single(c=>c.Name=="PendantDock");
            var cube=nodes.OfType<ViewCube>().Single();
            var camera=nodes.OfType<Camera3D>().Single();
            var controller=nodes.OfType<RobotController>().Single();controller.SetPhysicsProcess(false);
            var effects=nodes.OfType<EffectsPanel>().Single();
            effects.Settings.ResolutionMode=RenderResolution.Native;effects.Settings.Taa=false;effects.Settings.DepthOfField=false;effects.ApplySettings(false);
            main._Process(.1);
            var telemetry=ui.GetChildren().OfType<PanelContainer>().Single(p=>Descendants(p).OfType<Label>().Any(l=>l.Text=="STANDSTILL"));
            var toolbar=ui.GetChildren().OfType<PanelContainer>().Single(p=>Descendants(p).OfType<Button>().Any(b=>b.Text=="Fit"));

            Require(GetWindow().ContentScaleMode==Window.ContentScaleModeEnum.Disabled&&Mathf.IsEqualApprox(GetWindow().ContentScaleFactor,1),
                "Shipping main disables root canvas stretching and fractional content scaling");
            Require(new RenderSettings().ResolutionMode==RenderResolution.Native,"Fresh installations use native 3D resolution");
            foreach(var size in new[]{new Vector2I(1280,720),new Vector2I(1440,900),new Vector2I(1920,1080)})
            {
                viewport.Size=size;await Settle();main._Process(.1);await Settle();
                string extent=$"{size.X}×{size.Y}";
                var bounds=new Rect2(Vector2.Zero,size);
                Require(ui.GetGlobalRect().Size.IsEqualApprox(size)&&viewport.GetStretchTransform().Scale.IsEqualApprox(Vector2.One),extent+" uses one UI pixel per render-surface pixel");
                Require(RenderResolution.GetRenderSize(viewport)==size&&Mathf.IsEqualApprox(viewport.Scaling3DScale,1),extent+" keeps the native 3D buffer at 100%");
                Require(!viewport.UseTaa&&camera.Attributes is CameraAttributesPractical {DofBlurNearEnabled:false,DofBlurFarEnabled:false},extent+" retains the sharp camera and antialiasing settings");
                Require(Descendants(ui).OfType<Control>().All(c=>c.Scale.IsEqualApprox(Vector2.One)),extent+" never shrinks controls or text through inherited control scaling");
                Require(Contains(bounds,welding.Panel.GetGlobalRect())&&Contains(bounds,dock.GetGlobalRect()),extent+" contains both workspace docks");
                Require(Contains(bounds,cube.GetGlobalRect())&&Contains(bounds,telemetry.GetGlobalRect())&&Contains(bounds,toolbar.GetGlobalRect()),extent+" contains view cube, telemetry and camera tools");
                Require(!Overlaps(telemetry.GetGlobalRect(),cube.GetGlobalRect()),extent+" gives telemetry and the view cube separate readable regions");
                Require(!Overlaps(welding.Panel.GetGlobalRect(),toolbar.GetGlobalRect())&&!Overlaps(dock.GetGlobalRect(),toolbar.GetGlobalRect()),extent+" keeps camera tools clear of both docks");
                Require(Contains(dock.GetGlobalRect(),Descendants(pendant).OfType<Button>().Single(b=>b.Name=="EmergencyStop").GetGlobalRect()),extent+" keeps emergency stop visible without scrolling");
                var formScroll=Descendants(welding.Panel).OfType<ScrollContainer>().Single(s=>s.Name=="PreparationScroll");
                Require(Contains(welding.Panel.GetGlobalRect(),formScroll.GetGlobalRect())&&formScroll.Size.Y>100,extent+" provides a contained usable scroll area for preparation");
                Require(Descendants(welding.Panel).OfType<SpinBox>().All(s=>WithinWidth(formScroll.GetGlobalRect(),s.GetGlobalRect())),extent+" keeps every numeric field inside the preparation width");
                var actions=Descendants(welding.Panel).OfType<Button>().Where(b=>b.Text is "GENERATE PROGRAM" or "SIMULATE" or "EXPORT").ToArray();
                Require(actions.Length==3&&actions.All(b=>Contains(welding.Panel.GetGlobalRect(),b.GetGlobalRect())&&!Overlaps(formScroll.GetGlobalRect(),b.GetGlobalRect())),extent+" keeps program actions fixed and accessible below the scrolling form");
                CheckReadableContainers(welding.Panel,extent+" preparation");CheckReadableContainers(pendant,extent+" pendant");

                float dockedOffset=camera.HOffset;main.SetPendantVisible(false);await Settle();
                Require(!main.PendantVisible&&!pendant.IsVisibleInTree()&&welding.EditingVisible,extent+" can hide the pendant independently of preparation");
                Require(Mathf.Abs(camera.HOffset-dockedOffset)>.01f,extent+" recenters the working view when the pendant is hidden");
                Require(Contains(bounds,cube.GetGlobalRect())&&!Overlaps(telemetry.GetGlobalRect(),cube.GetGlobalRect()),extent+" relocates view controls cleanly after hiding the pendant");
                welding.SetPanelVisible(false);await Settle();
                Require(!welding.EditingVisible&&Mathf.Abs(camera.HOffset)<.0001f,extent+" centers the camera with both docks hidden");
                welding.SetPanelVisible(true);main.SetPendantVisible(true);await Settle();
                Require(main.PendantVisible&&welding.EditingVisible&&Mathf.IsEqualApprox(camera.HOffset,dockedOffset),extent+" restores the original docked camera framing");
            }
            await CheckHideMotion(main,welding,pendant,controller,ui);
            GD.Print($"PASS: {_checks} native workspace layout and dock interaction checks");
        }
        catch(Exception error){exitCode=1;GD.PrintErr("FAIL: "+error);}
        finally{viewport?.Free();}
        GetTree().Quit(exitCode);
    }

    private async Task CheckHideMotion(Main main,WeldingWorkspace welding,PendantPanel pendant,RobotController controller,Control ui)
    {
        controller.SetDrives(true);controller.SetJointJog(0,1);
        Require(controller.MotionActive,"Fixture begins actual robot motion before hiding its controls");
        Descendants(pendant).OfType<Button>().Single(b=>b.Name=="HidePendant").EmitSignal(BaseButton.SignalName.Pressed);await Settle();
        Require(!main.PendantVisible&&!controller.MotionActive&&controller.DrivesEnabled,"Pendant Hide button stops motion while retaining drive state");
        var toggle=Descendants(ui).OfType<Button>().Single(b=>b.Name=="TogglePendant");toggle.EmitSignal(BaseButton.SignalName.Pressed);await Settle();
        Require(main.PendantVisible&&!controller.MotionActive,"Toolbar reopens pendant without restarting stopped motion");
        main._UnhandledInput(new InputEventKey {Keycode=Key.P,Pressed=true});await Settle();
        Require(!main.PendantVisible,"P keyboard shortcut hides the pendant");
        main._UnhandledInput(new InputEventKey {Keycode=Key.P,Pressed=true});await Settle();
        Require(main.PendantVisible,"P keyboard shortcut restores the pendant");
        int notifications=0;welding.PanelVisibilityChanged+=()=>notifications++;
        Descendants(welding.Panel).OfType<Button>().Single(b=>b.TooltipText=="Hide weld preparation").EmitSignal(BaseButton.SignalName.Pressed);await Settle();
        Require(!welding.EditingVisible&&notifications==1,"Preparation close action notifies the responsive shell");
        Descendants(ui).OfType<Button>().Single(b=>b.Name=="ToggleWorkpiece").EmitSignal(BaseButton.SignalName.Pressed);await Settle();
        Require(welding.EditingVisible&&notifications==2,"Workpiece toolbar action restores preparation and camera layout");
    }

    private void CheckReadableContainers(Control panel,string label)
    {
        string? overlap=null;
        foreach(var container in Descendants(panel).OfType<Container>().Where(c=>c.IsVisibleInTree()&&c is not ScrollContainer))
        {
            var children=container.GetChildren().OfType<Control>().Where(c=>c.IsVisibleInTree()&&c.Size.X>0&&c.Size.Y>0).ToArray();
            for(int i=0;i<children.Length;i++)for(int j=i+1;j<children.Length;j++)
                if(Overlaps(children[i].GetGlobalRect(),children[j].GetGlobalRect()))overlap=$"{children[i].GetPath()} / {children[j].GetPath()}";
        }
        Require(overlap==null,label+" has no overlapping container controls"+(overlap==null?"":": "+overlap));
        Require(Descendants(panel).OfType<Label>().Where(l=>l.IsVisibleInTree()).All(l=>l.GetThemeFontSize("font_size")>=12),label+" never reduces labels below 12 native pixels");
        Require(Descendants(panel).OfType<LineEdit>().All(l=>l.GetThemeFontSize("font_size")>=14),label+" renders numeric input at readable native size");
    }

    private static bool WithinWidth(Rect2 outer,Rect2 inner)=>inner.Position.X>=outer.Position.X-1&&inner.End.X<=outer.End.X+1;
    private static bool Contains(Rect2 outer,Rect2 inner)=>WithinWidth(outer,inner)&&inner.Position.Y>=outer.Position.Y-1&&inner.End.Y<=outer.End.Y+1;
    private static bool Overlaps(Rect2 a,Rect2 b)=>Mathf.Min(a.End.X,b.End.X)-Mathf.Max(a.Position.X,b.Position.X)>.5f&&Mathf.Min(a.End.Y,b.End.Y)-Mathf.Max(a.Position.Y,b.Position.Y)>.5f;
    private async Task Settle(){for(int i=0;i<4;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);}
    private static IEnumerable<Node> Descendants(Node node){foreach(Node child in node.GetChildren()){yield return child;foreach(Node nested in Descendants(child))yield return nested;}}
    private void Require(bool pass,string message){if(!pass)throw new InvalidOperationException(message);_checks++;}
}
