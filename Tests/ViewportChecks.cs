using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EstunStudio;

/// <summary>Exercise the actual cube pointer targets and environment controls in Godot.</summary>
public partial class ViewportChecks : Node
{
    private int _checks;

    public override async void _Ready()
    {
        EffectsPanel? effects=null;
        const string configPath="user://render-settings.cfg";
        bool configExisted=Godot.FileAccess.FileExists(configPath);
        string originalConfig=configExisted?Godot.FileAccess.GetFileAsString(configPath):"";
        int result=0;
        try
        {
            CheckSettingsMigration(configPath);
            var layer=new CanvasLayer();AddChild(layer);
            var cube=new ViewCube();layer.AddChild(cube);cube.Build();
            var requested=new HashSet<string>();
            Vector3 selected=Vector3.Zero;
            void Requested(Vector3 direction){selected=direction;requested.Add(Key(direction));}
            cube.ViewRequested+=Requested;cube.PresetRequested+=Requested;
            foreach(var direction in new[]{Vector3.Right,Vector3.Left,Vector3.Up,Vector3.Down,Vector3.Back,Vector3.Forward})
            {
                cube.SetCameraBasis(Basis.LookingAt(-direction,Mathf.Abs(direction.Y)>.9f?Vector3.Back:Vector3.Up));
                await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                foreach(float x in new[]{-1f,0,1f})foreach(float y in new[]{-1f,0,1f})
                {
                    var point=new Vector2(52+x*22.36f,52-y*22.36f);
                    cube._GuiInput(new InputEventMouseButton {Position=point,ButtonIndex=MouseButton.Left,Pressed=true});
                    cube._GuiInput(new InputEventMouseButton {Position=point,ButtonIndex=MouseButton.Left,Pressed=false});
                    Require(selected.IsFinite()&&Mathf.Abs(selected.Length()-1)<.0001f,"Cube direction is normalized");
                    if(x==0&&y==0)Require(selected.IsEqualApprox(direction),"Face center selects named orientation");
                }
            }
            Require(requested.Count==26,$"All 26 SwissCAM view orientations can be clicked (actual {requested.Count})");
            int homes=0;cube.HomeRequested+=()=>homes++;
            cube._GuiInput(new InputEventKey{Keycode=Godot.Key.Home,Pressed=true});Require(homes==1,"Home resets the cube");

            var world=new StudioWorld();AddChild(world);world.Build();
            var camera=new Camera3D {Position=new Vector3(3,2,3)};AddChild(camera);
            CheckReflectionIsolation(world,camera);
            effects=new EffectsPanel();layer.AddChild(effects);effects.Build(world,camera);effects.ResetToCrisp();
            Require(GetViewport().Msaa3D==Viewport.Msaa.Msaa8X && !GetViewport().UseTaa && Mathf.IsEqualApprox(GetViewport().Scaling3DScale,1),"Crisp defaults use native-resolution 8× MSAA without temporal blur");
            var attributes=(CameraAttributesPractical)camera.Attributes;
            Require(!attributes.DofBlurNearEnabled && !attributes.DofBlurFarEnabled && attributes.DofBlurAmount==0,"Robot is fully in focus by default");
            Require(!world.Environment.FogEnabled,"Inspection defaults have no haze");
            Require(!world.Environment.SsrEnabled && world.Environment.SsilEnabled,"Maximum-quality indirect lighting complements stable studio reflections");
            var s=effects.Settings;s.Ssao=false;s.Ssil=false;s.Ssr=false;s.Bloom=false;s.Shadows=false;s.Fog=true;s.Exposure=1.31f;s.BloomStrength=.55f;s.Msaa=3;s.Taa=true;s.DepthOfField=true;
            s.Sparks=false;s.Smoke=false;s.BeadDetail=0;
            int notifications=0;effects.SettingsChanged+=_=>notifications++;effects.ApplySettings(false);
            Require(!world.Environment.SsaoEnabled && !world.Environment.SsilEnabled && !world.Environment.SsrEnabled,"All screen-space effects respond immediately");
            Require(!world.Environment.GlowEnabled && Mathf.IsEqualApprox(world.Environment.GlowIntensity,.55f) && Mathf.IsEqualApprox(world.Environment.TonemapExposure,1.31f),"Bloom and exposure sliders update environment properties");
            Require(world.Environment.FogEnabled && GetViewport().UseTaa && GetViewport().Msaa3D==Viewport.Msaa.Msaa8X,"Haze, TAA and 8× MSAA can be enabled explicitly");
            Require(attributes.DofBlurFarEnabled && attributes.DofBlurFarDistance>4,"Optional DOF keeps the working envelope in focus");
            Require(Descendants(world).OfType<Light3D>().All(light=>!light.ShadowEnabled),"Shadow control reaches all studio lights");
            Require(notifications==1,"Welding renderer receives changed settings");
            s.Save();var loaded=RenderSettings.Load();
            Require(!loaded.Sparks && !loaded.Smoke && loaded.BeadDetail==0 && loaded.Taa && loaded.Msaa==3 && Mathf.IsEqualApprox(loaded.Exposure,1.31f),"Render and welding settings persist across reloads");
            s.Ssr=true;s.Ssil=true;effects.ApplySettings(false);s.Save();loaded=RenderSettings.Load();
            Require(world.Environment.SsrEnabled && world.Environment.SsilEnabled && loaded.Ssr && loaded.Ssil,"Explicit screen-space effect opt-ins apply immediately and survive reload");
            effects.ResetToCrisp();
            Require(world.Environment.SsaoEnabled && world.Environment.SsilEnabled && !world.Environment.SsrEnabled && !GetViewport().UseTaa,"Reset restores stable studio reflections without temporal blur");
            GD.Print($"PASS: {_checks} view cube and render controls checks");
        }
        catch(Exception ex){result=1;GD.PrintErr($"FAIL: {ex}");}
        finally
        {
            effects?.Free();
            if(configExisted){using var file=Godot.FileAccess.Open(configPath,Godot.FileAccess.ModeFlags.Write);file.StoreString(originalConfig);}
            else if(Godot.FileAccess.FileExists(configPath)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(configPath));
        }
        GetTree().Quit(result);
    }

    private void CheckSettingsMigration(string path)
    {
        var legacy=new ConfigFile();
        legacy.SetValue("render","ssr",true);legacy.SetValue("render","ssil",true);
        legacy.SetValue("render","ssao",false);legacy.SetValue("render","exposure",1.27f);
        legacy.SetValue("render","bloom",false);legacy.SetValue("render","bloom_strength",.38f);
        legacy.SetValue("render","taa",true);legacy.SetValue("render","msaa",3);
        legacy.SetValue("welding","smoke",false);legacy.SetValue("welding","bead_detail",1);
        Require(legacy.Save(path)==Error.Ok,"Legacy rendering configuration fixture is written");
        var migrated=RenderSettings.Load();
        Require(!migrated.Ssr && migrated.Ssil,"Upgrade enables maximum-quality indirect light while retaining stable reflections");
        Require(migrated.Ssao && Mathf.IsEqualApprox(migrated.Exposure,1.27f) && !migrated.Bloom && Mathf.IsEqualApprox(migrated.BloomStrength,.38f) && !migrated.Taa && migrated.Msaa==3 && migrated.ResolutionMode==RenderResolution.Native && !migrated.DepthOfField && !migrated.Smoke && migrated.BeadDetail==1,
            "Sharp migration removes blur/upscaling while preserving exposure and welding preferences");
        migrated.Save();
        var current=RenderSettings.Load();
        Require(!current.Ssr && current.Ssil && Mathf.IsEqualApprox(current.Exposure,1.27f),"Migrated settings persist across a second load");
        current.Ssr=true;current.Ssil=true;current.Save();
        var optedIn=RenderSettings.Load();
        Require(optedIn.Ssr && optedIn.Ssil,"Current-format user opt-ins are not overwritten by migration");
    }

    private void CheckReflectionIsolation(StudioWorld world,Camera3D camera)
    {
        var probe=Descendants(world).OfType<ReflectionProbe>().Single();
        var staticMeshes=Descendants(world).OfType<MeshInstance3D>().Where(mesh=>(mesh.Layers&probe.CullMask)!=0).ToArray();
        Require(staticMeshes.Length>100,"Studio reflection probe captures the static room architecture");
        var plinth=world.GetChildren().Single(node=>node.Name=="Instrument plinth");
        Require(Descendants(plinth).OfType<VisualInstance3D>().All(visual=>(visual.Layers&probe.CullMask)==0),"Mounting platform cannot produce parallax-inaccurate self-reflections in the room probe");
        Require(Descendants(world.TcpAxes).OfType<VisualInstance3D>().All(visual=>(visual.Layers&probe.CullMask)==0),"TCP coordinate markers cannot be frozen into studio reflections");
        var robot=new RobotModel();world.RobotMount.AddChild(robot);robot.Build();
        var robotMeshes=Descendants(robot).OfType<MeshInstance3D>().ToArray();
        Require(robotMeshes.Length>0 && robotMeshes.All(mesh=>(mesh.Layers&probe.CullMask)==0 && (mesh.Layers&probe.ReflectionMask)!=0),
            "Articulated robot receives studio reflections without appearing in the static probe capture");
        var document=new CadDocument {
            Vertices=new[]{Vector3.Zero,Vector3.Right,Vector3.Back},
            Normals=new[]{Vector3.Up,Vector3.Up,Vector3.Up},Indices=new[]{0,2,1},
            Seams=new(){new CadSeam {Id="probe-check",Points=new[]{Vector3.Zero,Vector3.Right},AngleDegrees=90,MinAngleDegrees=90,MaxAngleDegrees=90}}
        };
        var part=new CadPart();world.AddChild(part);part.Build(document,camera);
        var partMeshes=Descendants(part).OfType<MeshInstance3D>().ToArray();
        Require(partMeshes.Length==2 && partMeshes.All(mesh=>(mesh.Layers&probe.CullMask)==0 && (mesh.Layers&probe.ReflectionMask)!=0),
            "Imported CAD and editable seam highlights cannot leave stale probe reflections");
        robot.Free();part.Free();
    }

    private static string Key(Vector3 v)=>$"{Mathf.Sign(Mathf.Snapped(v.X,.01f))},{Mathf.Sign(Mathf.Snapped(v.Y,.01f))},{Mathf.Sign(Mathf.Snapped(v.Z,.01f))}";
    private static IEnumerable<Node> Descendants(Node node)
    {foreach(Node child in node.GetChildren()){yield return child;foreach(var grandchild in Descendants(child))yield return grandchild;}}
    private void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException(message);_checks++;}
}
