using Godot;

namespace EstunStudio;

/// <summary>Persisted, explicit quality controls shared by the studio and welding renderer.</summary>
public sealed class RenderSettings
{
    private const string FilePath = "user://render-settings.cfg";
    public const int RenderingRevision = 2;
    public bool Ssao { get; set; } = true;
    public bool Ssil { get; set; } = false;
    public bool Ssr { get; set; } = false;
    public bool Bloom { get; set; } = true;
    public float BloomStrength { get; set; } = .22f;
    public float Exposure { get; set; } = .92f;
    public bool Shadows { get; set; } = true;
    public bool Fog { get; set; } = false;
    /// <summary>Viewport.Msaa value: 0 off, 1 2×, 2 4×, 3 8×.</summary>
    public int Msaa { get; set; } = 2;
    public bool Taa { get; set; } = false;
    public bool DepthOfField { get; set; } = false;
    /// <summary>0: fixed pixel budget, 1: native display resolution, 2: manual spatial scale.</summary>
    public int ResolutionMode { get; set; } = RenderResolution.Auto;
    public float ManualRenderScale { get; set; } = 1f;
    public bool Sparks { get; set; } = true;
    public bool Smoke { get; set; } = true;
    /// <summary>Weld bead detail: 0 preview, 1 high, 2 ultra.</summary>
    public int BeadDetail { get; set; } = 2;

    public static RenderSettings Load()
    {
        var settings=new RenderSettings();
        var config=new ConfigFile();
        if(config.Load(FilePath)!=Error.Ok) return settings;
        settings.Ssao=config.GetValue("render","ssao",settings.Ssao).AsBool();
        settings.Ssil=config.GetValue("render","ssil",settings.Ssil).AsBool();
        settings.Ssr=config.GetValue("render","ssr",settings.Ssr).AsBool();
        settings.Bloom=config.GetValue("render","bloom",settings.Bloom).AsBool();
        settings.BloomStrength=Mathf.Clamp(config.GetValue("render","bloom_strength",settings.BloomStrength).AsSingle(),0,1.2f);
        settings.Exposure=Mathf.Clamp(config.GetValue("render","exposure",settings.Exposure).AsSingle(),.4f,1.8f);
        settings.Shadows=config.GetValue("render","shadows",settings.Shadows).AsBool();
        settings.Fog=config.GetValue("render","fog",settings.Fog).AsBool();
        settings.Msaa=Mathf.Clamp(config.GetValue("render","msaa",settings.Msaa).AsInt32(),0,3);
        settings.Taa=config.GetValue("render","taa",settings.Taa).AsBool();
        settings.DepthOfField=config.GetValue("render","depth_of_field",settings.DepthOfField).AsBool();
        settings.ResolutionMode=Mathf.Clamp(config.GetValue("render","resolution_mode",settings.ResolutionMode).AsInt32(),0,2);
        float manualScale=config.GetValue("render","manual_render_scale",settings.ManualRenderScale).AsSingle();
        settings.ManualRenderScale=float.IsFinite(manualScale)?Mathf.Clamp(manualScale,RenderResolution.MinimumScale,1):1;
        settings.Sparks=config.GetValue("welding","sparks",settings.Sparks).AsBool();
        settings.Smoke=config.GetValue("welding","smoke",settings.Smoke).AsBool();
        settings.BeadDetail=Mathf.Clamp(config.GetValue("welding","bead_detail",settings.BeadDetail).AsInt32(),0,2);
        // Replace the previous unstable screen-space defaults once, including
        // existing installations. Other operator preferences remain intact.
        if(config.GetValue("render","revision",0).AsInt32()<RenderingRevision)
        {
            settings.Ssr=false;settings.Ssil=false;
            settings.Save();
        }
        return settings;
    }

    public void Save()
    {
        var config=new ConfigFile();
        config.SetValue("render","revision",RenderingRevision);
        config.SetValue("render","ssao",Ssao);config.SetValue("render","ssil",Ssil);config.SetValue("render","ssr",Ssr);
        config.SetValue("render","bloom",Bloom);config.SetValue("render","bloom_strength",BloomStrength);
        config.SetValue("render","exposure",Exposure);config.SetValue("render","shadows",Shadows);config.SetValue("render","fog",Fog);
        config.SetValue("render","msaa",Msaa);config.SetValue("render","taa",Taa);config.SetValue("render","depth_of_field",DepthOfField);
        config.SetValue("render","resolution_mode",ResolutionMode);config.SetValue("render","manual_render_scale",ManualRenderScale);
        config.SetValue("welding","sparks",Sparks);config.SetValue("welding","smoke",Smoke);config.SetValue("welding","bead_detail",BeadDetail);
        var error=config.Save(FilePath);
        if(error!=Error.Ok) GD.PushWarning($"Could not save rendering settings: {error}");
    }
}
