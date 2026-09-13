using Godot;
using System;

namespace EstunStudio;

/// <summary>Checks the full-screen 3D pixel budget independently of desktop size or GPU speed.</summary>
public partial class RenderResolutionChecks : Node
{
    private int _checks;

    public override async void _Ready()
    {
        const string configPath="user://render-settings.cfg";
        bool configExisted=Godot.FileAccess.FileExists(configPath);
        string originalConfig=configExisted?Godot.FileAccess.GetFileAsString(configPath):"";
        SubViewport? viewport=null;
        int result=0;
        try
        {
            CheckScalePolicy();
            viewport=new SubViewport {
                Size=new Vector2I(3840,2160),
                Size2DOverride=new Vector2I(1600,900),
                Size2DOverrideStretch=true,
                OwnWorld3D=true
            };
            AddChild(viewport);
            var canvas=new CanvasLayer();viewport.AddChild(canvas);
            var workspace=new Control {Position=new Vector2(16,24),Size=new Vector2(1450,830)};
            canvas.AddChild(workspace);
            var camera=new Camera3D {Position=new Vector3(3,2,3),Current=true};viewport.AddChild(camera);
            camera.LookAt(Vector3.Zero);
            await Frame();
            await Frame();
            Require(RenderResolution.GetRenderSize(viewport)==new Vector2I(3840,2160),
                "Physical render size includes canvas stretch rather than using the logical UI size");
            Require(viewport.GetVisibleRect().Size.IsEqualApprox(new Vector2(1600,900)),
                "Fixture retains an independent logical canvas at 4K");
            CheckViewportInvariance(viewport,workspace,camera);

            var world=new StudioWorld();viewport.AddChild(world);world.Build();
            CheckReflectionAtlas(world);
            var effects=new EffectsPanel();workspace.AddChild(effects);effects.Build(world,camera);
            var settings=effects.Settings;
            settings.ResolutionMode=0;
            settings.ManualRenderScale=.65f;
            settings.Ssr=false;settings.Ssil=false;settings.Exposure=1.17f;
            settings.Msaa=1;settings.Taa=false;settings.DepthOfField=false;
            settings.Sparks=false;settings.Smoke=false;settings.BeadDetail=1;
            effects.ApplySettings(false);
            Require(!effects.Visible,"Resolution resize handling is exercised with the effects panel closed");
            Near(viewport.Scaling3DScale,.3952847f,"Effects controls apply the 4K automatic budget");
            foreach(var size in new[]{new Vector2I(1440,900),new Vector2I(2560,1440),new Vector2I(3840,2160)})
            {
                viewport.Size=size;
                await Frame();
                await Frame();
                Require(RenderResolution.GetRenderSize(viewport)==size,"Render extent updates after a live viewport resize");
                Near(viewport.Scaling3DScale,RenderResolution.CalculateScale(size,0),
                    "Hidden effects panel reapplies the automatic scale after resizing");
            }
            Require(!world.Environment.SsrEnabled && !world.Environment.SsilEnabled &&
                Mathf.IsEqualApprox(world.Environment.TonemapExposure,1.17f) &&
                viewport.Msaa3D==Viewport.Msaa.Msaa2X && !viewport.UseTaa &&
                !settings.Sparks && !settings.Smoke && settings.BeadDetail==1,
                "Window resizing preserves lighting, anti-aliasing and welding preferences");
            settings.ResolutionMode=1;effects.ApplySettings(false);
            Near(viewport.Scaling3DScale,1,"Native mode restores full resolution immediately");
            settings.ResolutionMode=2;effects.ApplySettings(false);
            Near(viewport.Scaling3DScale,.65f,"Manual mode applies the operator's selected scale immediately");
            viewport.Size=new Vector2I(1920,1080);
            await Frame();await Frame();
            Near(viewport.Scaling3DScale,.65f,"Manual scale stays fixed when the window changes size");
            CheckPersistence(settings,configPath);
            GD.Print($"PASS: {_checks} render resolution and full-screen regression checks");
        }
        catch(Exception ex){result=1;GD.PrintErr($"FAIL: {ex}");}
        finally
        {
            // Free the panel before restoring the user's configuration, including its pending save.
            viewport?.Free();
            if(configExisted)
            {
                using var file=Godot.FileAccess.Open(configPath,Godot.FileAccess.ModeFlags.Write);
                file.StoreString(originalConfig);
            }
            else if(Godot.FileAccess.FileExists(configPath))
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(configPath));
        }
        GetTree().Quit(result);
    }

    private void CheckScalePolicy()
    {
        Near(RenderResolution.CalculateScale(new Vector2I(1440,900),0),1,
            "The existing 1440×900 window remains at native quality");
        Near(RenderResolution.CalculateScale(new Vector2I(1120,700),0),1,
            "Small windows are never supersampled");
        Near(RenderResolution.CalculateScale(new Vector2I(1920,1080),0),.7905694f,
            "1080p uses the same pixel budget as the original window");
        Near(RenderResolution.CalculateScale(new Vector2I(2560,1440),0),.5929271f,
            "1440p uses the same pixel budget as the original window");
        Near(RenderResolution.CalculateScale(new Vector2I(3840,2160),0),.3952847f,
            "4K does not silently quadruple the 1080p rendering workload");
        foreach(var size in new[]{new Vector2I(1920,1080),new Vector2I(2560,1440),new Vector2I(3840,2160),
            new Vector2I(1080,1920),new Vector2I(3440,1440),new Vector2I(5120,1440)})
        {
            float scale=RenderResolution.CalculateScale(size,0);
            double pixels=(double)size.X*size.Y*scale*scale;
            Require(Math.Abs(pixels-RenderResolution.DefaultPixelBudget)<1,
                $"Automatic pixel budget is stable for {size.X}×{size.Y}, including portrait and ultrawide");
            Near(RenderResolution.CalculateScale(size,1),1,"Native mode is independent of display dimensions");
            Near(RenderResolution.CalculateScale(size,2,.63f),.63f,"Manual mode is independent of display dimensions");
        }
        Near(RenderResolution.CalculateScale(new Vector2I(3840,2160),2,-1),.25f,"Manual scale has a valid lower bound");
        Near(RenderResolution.CalculateScale(new Vector2I(3840,2160),2,2),1,"Manual scale has a valid upper bound");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity})
            Near(RenderResolution.CalculateScale(new Vector2I(3840,2160),2,invalid),1,
                "Non-finite manual settings cannot reach the renderer");
        foreach(var size in new[]{Vector2I.Zero,new Vector2I(0,900),new Vector2I(1440,0),new Vector2I(-1,900)})
            Near(RenderResolution.CalculateScale(size,0),1,"Missing or minimized render extent returns a finite safe scale");
        Near(RenderResolution.CalculateScale(new Vector2I(int.MaxValue,int.MaxValue),0),.25f,
            "Large render dimensions do not overflow the pixel calculation");
        Near(RenderResolution.CalculateScale(new Vector2I(3840,2160),-1),.3952847f,
            "Unknown resolution modes fall back to automatic");
    }

    private void CheckReflectionAtlas(StudioWorld world)
    {
        int slots=ProjectSettings.GetSetting("rendering/reflections/reflection_atlas/reflection_count",64).AsInt32();
        int resolution=ProjectSettings.GetSetting("rendering/reflections/reflection_atlas/reflection_size",256).AsInt32();
        int probes=CountProbes(world);
        Require(probes>0 && slots>=probes,
            "Reflection atlas has capacity for every studio reflection probe");
        Require(slots<=4,
            "Reflection atlas cannot reserve the default 64 slots and exhaust GPU memory when maximized");
        Require(resolution==1024,
            "Reducing unused reflection slots preserves the studio's 1024-pixel reflection detail");
        // Six RGBA16F faces (8 bytes/texel) and the complete mip pyramid (4/3).
        // At 1024, 64 slots consume 4 GiB; the studio's four slots consume 256 MiB.
        double estimatedBytes=(double)slots*resolution*resolution*6*8*4/3;
        Require(estimatedBytes>0 && estimatedBytes<=256d*1024*1024,
            "Estimated HDR reflection atlas allocation stays within its 256 MiB budget");

        static int CountProbes(Node node)
        {
            int count=node is ReflectionProbe?1:0;
            foreach(Node child in node.GetChildren())count+=CountProbes(child);
            return count;
        }
    }

    private void CheckViewportInvariance(SubViewport viewport,Control workspace,Camera3D camera)
    {
        var settings=new RenderSettings {ResolutionMode=1};
        viewport.UseTaa=true;
        viewport.Msaa3D=Viewport.Msaa.Msaa4X;
        RenderResolution.Apply(viewport,settings);
        var canvasRect=viewport.GetVisibleRect();
        var uiRect=workspace.GetGlobalRect();
        var stretch=viewport.GetStretchTransform();
        Vector3 worldPoint=new(.3f,.2f,.1f);
        Vector2 projected=camera.UnprojectPosition(worldPoint);
        Vector3 rayOrigin=camera.ProjectRayOrigin(projected),rayDirection=camera.ProjectRayNormal(projected);
        foreach(int mode in new[]{0,2,1})
        {
            settings.ResolutionMode=mode;settings.ManualRenderScale=.55f;
            RenderResolution.Apply(viewport,settings);
            Require(viewport.GetVisibleRect()==canvasRect && workspace.GetGlobalRect()==uiRect &&
                viewport.GetStretchTransform().IsEqualApprox(stretch),
                "Resolution controls preserve the pendant canvas, layout and UI stretch");
            Require(camera.UnprojectPosition(worldPoint).IsEqualApprox(projected) &&
                camera.ProjectRayOrigin(projected).IsEqualApprox(rayOrigin) &&
                camera.ProjectRayNormal(projected).IsEqualApprox(rayDirection),
                "CAD gizmo projection and picking rays stay aligned when 3D resolution changes");
            Require(viewport.UseTaa && viewport.Msaa3D==Viewport.Msaa.Msaa4X,
                "Changing resolution leaves explicit temporal and multisample AA choices intact");
            Require(viewport.Scaling3DMode==(mode==1?Viewport.Scaling3DModeEnum.Bilinear:Viewport.Scaling3DModeEnum.Fsr),
                "Sub-native rendering uses spatial FSR while native rendering bypasses it");
        }
        Near(viewport.FsrSharpness,.2f,"Spatial reconstruction has the intended sharpness");
    }

    private void CheckPersistence(RenderSettings settings,string path)
    {
        settings.ResolutionMode=2;settings.ManualRenderScale=.65f;settings.Save();
        var loaded=RenderSettings.Load();
        Require(loaded.ResolutionMode==2 && Mathf.IsEqualApprox(loaded.ManualRenderScale,.65f),
            "Manual mode and scale persist across application restarts");
        Require(!loaded.Ssr && !loaded.Ssil && Mathf.IsEqualApprox(loaded.Exposure,1.17f) &&
            loaded.Msaa==1 && !loaded.Taa && !loaded.Sparks && !loaded.Smoke && loaded.BeadDetail==1,
            "Persisting resolution preserves the operator's other render and welding settings");
        settings.ResolutionMode=1;settings.Save();loaded=RenderSettings.Load();
        Require(loaded.ResolutionMode==1 && Mathf.IsEqualApprox(loaded.ManualRenderScale,.65f),
            "Native selection persists and retains the previous manual scale");
        var previous=new ConfigFile();
        previous.SetValue("render","revision",RenderSettings.RenderingRevision);
        previous.SetValue("render","exposure",1.09f);previous.SetValue("render","msaa",3);
        previous.SetValue("render","ssr",true);previous.SetValue("welding","smoke",false);
        Require(previous.Save(path)==Error.Ok,"Previous-version configuration fixture is written");
        loaded=RenderSettings.Load();
        Require(loaded.ResolutionMode==1 && Mathf.IsEqualApprox(loaded.ManualRenderScale,1),
            "Default rendering is native resolution without requiring a settings reset");
        Require(Mathf.IsEqualApprox(loaded.Exposure,1.09f) && loaded.Msaa==3 && loaded.Ssr && !loaded.Smoke,
            "Adding automatic resolution keeps existing effect preferences");
    }

    private async System.Threading.Tasks.Task Frame()=>await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
    private void Near(float actual,float expected,string description)=>Require(
        float.IsFinite(actual) && Math.Abs(actual-expected)<.00001f,$"{description} (actual {actual:F6})");
    private void Require(bool condition,string description)
    {
        if(!condition)throw new InvalidOperationException(description);
        _checks++;
    }
}
