using Godot;
using System;
using System.Collections.Generic;

namespace EstunStudio;

/// <summary>Live rendering controls with persistent settings and sharp engineering defaults.</summary>
public partial class EffectsPanel : PanelContainer
{
    public RenderSettings Settings { get; private set; } = new();
    public event Action<RenderSettings>? SettingsChanged;
    private StudioWorld _world = null!;
    private Camera3D _camera = null!;
    private readonly List<Action> _refresh = new();
    private bool _syncing, _built, _dirty;
    private double _saveDelay;
    private VBoxContainer _rows = null!;
    private Vector2I _renderSize;
    private int _resolutionMode=-1;
    private float _manualRenderScale=-1;
    private Label _resolutionInfo=null!;
    private readonly Color _ink=new("e3e9e9"), _muted=new("8d9fa9"), _amber=new("f4b658");

    public void Build(StudioWorld world, Camera3D camera)
    {
        if(_built) return;
        _built=true;_world=world;_camera=camera;
        Name="RenderEffects";
        Position=new Vector2(689,168);
        Size=CustomMinimumSize=new Vector2(420,600);
        MouseFilter=MouseFilterEnum.Stop;
        ZIndex=20;
        var panel=new StyleBoxFlat { BgColor=new Color("1b252d"),BorderColor=new Color("44515b"),
            BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,
            CornerRadiusBottomLeft=12,CornerRadiusBottomRight=12,CornerRadiusTopLeft=12,CornerRadiusTopRight=12,
            ShadowSize=20,ShadowColor=new Color(0,0,0,.35f) };
        AddThemeStyleboxOverride("panel",panel);
        var margin=new MarginContainer();AddChild(margin);
        foreach(string side in new[]{"left","right","top","bottom"}) margin.AddThemeConstantOverride("margin_"+side,20);
        var content=new VBoxContainer();margin.AddChild(content);content.AddThemeConstantOverride("separation",12);
        var header=new HBoxContainer();content.AddChild(header);
        var title=Label("VISUAL EFFECTS",18,_ink);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);
        var close=Button("×",31);header.AddChild(close);close.Pressed+=Hide;
        var intro=Label("Tune the studio and welding simulation.",11,_muted);content.AddChild(intro);
        var scroll=new ScrollContainer { CustomMinimumSize=new Vector2(374,400),SizeFlagsVertical=SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled };
        content.AddChild(scroll);
        _rows=new VBoxContainer { SizeFlagsHorizontal=SizeFlags.ExpandFill };scroll.AddChild(_rows);_rows.AddThemeConstantOverride("separation",3);
        Settings=RenderSettings.Load();
        Section("DISPLAY PERFORMANCE");
        OptionRow("3D resolution",new[]{"Auto","Native","Manual"},()=>Settings.ResolutionMode,v=>Settings.ResolutionMode=v,"Auto keeps the 3D workload near a 1440 × 900 window using spatial FSR. UI and CAD controls remain full resolution. Native uses every display pixel; Manual uses the scale below.");
        SliderRow("Manual 3D scale",.25f,1,.01f,()=>Settings.ManualRenderScale,v=>Settings.ManualRenderScale=v);
        _resolutionInfo=Label("",10,_muted);_rows.AddChild(_resolutionInfo);
        Section("LIGHT & MATERIAL");
        ToggleRow("Contact shadows / SSAO",()=>Settings.Ssao,v=>Settings.Ssao=v,"Ambient occlusion adds contact depth around close surfaces.");
        ToggleRow("Screen-space bounce (optional)",()=>Settings.Ssil,v=>Settings.Ssil=v,"Optional screen-space indirect light can introduce noise. HDR studio lighting remains active when this is off.");
        ToggleRow("Screen reflections (optional)",()=>Settings.Ssr,v=>Settings.Ssr=v,"Optional screen-space reflections can show gaps at object silhouettes and screen edges. Stable HDR and studio reflections remain active when this is off.");
        ToggleRow("Soft shadows",()=>Settings.Shadows,v=>Settings.Shadows=v,"Enable shadows from the studio softboxes and ceiling light.");
        ToggleRow("Atmospheric haze",()=>Settings.Fog,v=>Settings.Fog=v,"Very light studio haze. Disabled by default for clear geometry.");
        ToggleRow("Bloom",()=>Settings.Bloom,v=>Settings.Bloom=v,"Glow around bright light sources and the welding arc.");
        SliderRow("Bloom strength",0,1.2f,.01f,()=>Settings.BloomStrength,v=>Settings.BloomStrength=v);
        SliderRow("Exposure",.4f,1.8f,.01f,()=>Settings.Exposure,v=>Settings.Exposure=v);
        Section("IMAGE CLARITY");
        OptionRow("Anti-aliasing / MSAA",new[]{"Off","2×","4×","8×"},()=>Settings.Msaa,v=>Settings.Msaa=v,"Multisampling smooths the CAD silhouette while preserving detail.");
        ToggleRow("Temporal anti-aliasing",()=>Settings.Taa,v=>Settings.Taa=v,"Accumulates previous frames to reduce shimmer. Can soften detail during motion.");
        ToggleRow("Depth of field",()=>Settings.DepthOfField,v=>Settings.DepthOfField=v,"Cinematic blur behind the robot. Disabled by default for inspection.");
        Section("WELDING SIMULATION");
        ToggleRow("Hot sparks",()=>Settings.Sparks,v=>Settings.Sparks=v,"Glowing molten sparks during an active welding pass.");
        ToggleRow("Welding smoke",()=>Settings.Smoke,v=>Settings.Smoke=v,"Rising translucent smoke around the welding arc.");
        OptionRow("Weld bead detail",new[]{"Preview","High","Ultra"},()=>Settings.BeadDetail,v=>Settings.BeadDetail=v,"Select the surface detail of deposited weld beads.");
        var bottom=new HBoxContainer();content.AddChild(bottom);
        var reset=Button("RESET TO CRISP",151);bottom.AddChild(reset);reset.Pressed+=ResetToCrisp;
        var saved=Label("Saved automatically",10,_muted);saved.SizeFlagsHorizontal=SizeFlags.ExpandFill;saved.HorizontalAlignment=HorizontalAlignment.Right;bottom.AddChild(saved);
        RefreshControls();ApplySettings(false);Hide();
    }

    public void Toggle() { Visible=!Visible;if(Visible) MoveToFront(); }

    public void ResetToCrisp()
    {
        Settings=new RenderSettings();RefreshControls();ApplySettings();
    }

    /// <summary>Apply every field to the active scene and notify the welding renderer.</summary>
    public void ApplySettings(bool save=true)
    {
        if(!_built) return;
        var env=_world.Environment;
        env.SsaoEnabled=Settings.Ssao;env.SsilEnabled=Settings.Ssil;env.SsrEnabled=Settings.Ssr;
        env.GlowEnabled=Settings.Bloom;env.GlowIntensity=Settings.BloomStrength;
        env.GlowBloom=.02f;env.GlowHdrThreshold=1.5f;env.TonemapExposure=Settings.Exposure;env.FogEnabled=Settings.Fog;
        var viewport=GetViewport();
        viewport.Msaa3D=(Viewport.Msaa)Settings.Msaa;
        viewport.UseTaa=Settings.Taa;
        viewport.ScreenSpaceAA=Viewport.ScreenSpaceAAEnum.Disabled;
        UpdateRenderResolution();
        ApplyLightShadows(_world,Settings.Shadows);
        if(_camera.Attributes is not CameraAttributesPractical) _camera.Attributes=new CameraAttributesPractical();
        var attributes=(CameraAttributesPractical)_camera.Attributes;
        attributes.DofBlurNearEnabled=false;
        attributes.DofBlurFarEnabled=Settings.DepthOfField;
        attributes.DofBlurFarTransition=4f;
        attributes.DofBlurAmount=Settings.DepthOfField ? .12f : 0;
        SetFocusDistance(attributes);
        SettingsChanged?.Invoke(Settings);
        if(save){_dirty=true;_saveDelay=.35;}
    }

    private void SetFocusDistance(CameraAttributesPractical attributes)
    {
        // Keep the complete working envelope in focus; blur only the background.
        attributes.DofBlurFarDistance=_camera.GlobalPosition.DistanceTo(_world.RobotMount.GlobalPosition+Vector3.Up*.8f)+2.2f;
    }

    private static void ApplyLightShadows(Node parent,bool enabled)
    {
        foreach(Node child in parent.GetChildren())
        {
            // Arc light has its own simulation lifecycle and does not cast softbox shadows.
            if(child is Light3D light && (child is SpotLight3D || child is DirectionalLight3D)) light.ShadowEnabled=enabled&&light.GetMeta("studio_casts_shadow",false).AsBool();
            ApplyLightShadows(child,enabled);
        }
    }

    public override void _Process(double delta)
    {
        if(!_built) return;
        Vector2I renderSize=RenderResolution.GetRenderSize(GetViewport());
        if(renderSize!=_renderSize || _resolutionMode!=Settings.ResolutionMode || !Mathf.IsEqualApprox(_manualRenderScale,Settings.ManualRenderScale))UpdateRenderResolution();
        if(Settings.DepthOfField && _camera.Attributes is CameraAttributesPractical attributes) SetFocusDistance(attributes);
        if(_dirty && (_saveDelay-=delta)<=0){Settings.Save();_dirty=false;}
    }

    public override void _ExitTree() { if(_dirty) Settings.Save(); }

    private void UpdateRenderResolution()
    {
        var viewport=GetViewport();RenderResolution.Apply(viewport,Settings);
        _renderSize=RenderResolution.GetRenderSize(viewport);_resolutionMode=Settings.ResolutionMode;_manualRenderScale=Settings.ManualRenderScale;
        float scale=viewport.Scaling3DScale;
        if(_resolutionInfo!=null)_resolutionInfo.Text=$"3D  {Mathf.RoundToInt(_renderSize.X*scale)} × {Mathf.RoundToInt(_renderSize.Y*scale)}  ·  {scale*100:0}%  ·  UI native";
    }

    private void RefreshControls()
    {
        _syncing=true;foreach(var refresh in _refresh)refresh();_syncing=false;
    }

    private Label Label(string text,int size,Color color)
    {
        var label=new Label {Text=text,MouseFilter=MouseFilterEnum.Ignore};
        label.AddThemeFontSizeOverride("font_size",Math.Max(12,size));label.AddThemeColorOverride("font_color",color);return label;
    }

    private void Section(string text)
    {
        var box=new VBoxContainer {CustomMinimumSize=new Vector2(0,37)};_rows.AddChild(box);
        box.AddThemeConstantOverride("separation",9);
        var line=new HSeparator();line.AddThemeStyleboxOverride("separator",new StyleBoxLine {Color=new Color("35444e"),Thickness=1});box.AddChild(line);
        box.AddChild(Label(text,10,_amber));
    }

    private HBoxContainer Row(string text,string tooltip="")
    {
        var row=new HBoxContainer {CustomMinimumSize=new Vector2(0,32),TooltipText=tooltip};_rows.AddChild(row);
        var caption=Label(text,12,_ink);caption.SizeFlagsHorizontal=SizeFlags.ExpandFill;row.AddChild(caption);
        row.AddThemeConstantOverride("separation",10);return row;
    }

    private void ToggleRow(string caption,Func<bool> get,Action<bool> set,string tooltip)
    {
        var row=Row(caption,tooltip);
        var toggle=Button("",62);toggle.ToggleMode=true;toggle.TooltipText=tooltip;row.AddChild(toggle);
        void Refresh(){toggle.ButtonPressed=get();toggle.Text=get()?"ON":"OFF";}
        _refresh.Add(Refresh);
        toggle.Toggled+=v=>{toggle.Text=v?"ON":"OFF";if(_syncing)return;set(v);ApplySettings();};
    }

    private void SliderRow(string caption,float minimum,float maximum,float step,Func<float> get,Action<float> set)
    {
        var row=Row(caption);
        var slider=new HSlider {MinValue=minimum,MaxValue=maximum,Step=step,CustomMinimumSize=new Vector2(105,25),SizeFlagsVertical=SizeFlags.ShrinkCenter};
        row.AddChild(slider);
        slider.AddThemeStyleboxOverride("slider",new StyleBoxFlat {BgColor=new Color("3c4a53"),ContentMarginTop=2,ContentMarginBottom=2,CornerRadiusTopLeft=2,CornerRadiusTopRight=2,CornerRadiusBottomLeft=2,CornerRadiusBottomRight=2});
        slider.AddThemeStyleboxOverride("grabber_area",new StyleBoxFlat {BgColor=_amber,ContentMarginTop=2,ContentMarginBottom=2});
        var number=Label("",11,_muted);number.CustomMinimumSize=new Vector2(36,24);number.HorizontalAlignment=HorizontalAlignment.Right;row.AddChild(number);
        _refresh.Add(()=>{slider.Value=get();number.Text=get().ToString("F2",System.Globalization.CultureInfo.InvariantCulture);});
        slider.ValueChanged+=v=>{number.Text=v.ToString("F2",System.Globalization.CultureInfo.InvariantCulture);if(_syncing)return;set((float)v);ApplySettings();};
    }

    private void OptionRow(string caption,string[] options,Func<int> get,Action<int> set,string tooltip)
    {
        var row=Row(caption,tooltip);
        var select=new OptionButton {CustomMinimumSize=new Vector2(96,29),FocusMode=FocusModeEnum.None,TooltipText=tooltip};
        row.AddChild(select);select.AddThemeFontSizeOverride("font_size",13);
        select.AddThemeStyleboxOverride("normal",ButtonStyle(new Color("293943"),new Color("475761")));
        select.AddThemeStyleboxOverride("hover",ButtonStyle(new Color("374852"),new Color("70838d")));
        select.AddThemeStyleboxOverride("pressed",ButtonStyle(new Color("6c512d"),_amber));
        foreach(var option in options) select.AddItem(option);
        _refresh.Add(()=>select.Selected=get());
        select.ItemSelected+=id=>{if(_syncing)return;set((int)id);ApplySettings();};
    }

    private Button Button(string text,float width)
    {
        var button=new Button {Text=text,CustomMinimumSize=new Vector2(width,29),FocusMode=FocusModeEnum.None,MouseDefaultCursorShape=CursorShape.PointingHand};
        button.AddThemeFontSizeOverride("font_size",13);button.AddThemeColorOverride("font_color",_muted);
        button.AddThemeColorOverride("font_hover_color",_ink);button.AddThemeColorOverride("font_pressed_color",new Color("f8d99d"));
        button.AddThemeStyleboxOverride("normal",ButtonStyle(new Color("26353f"),new Color("3b4e5a")));
        button.AddThemeStyleboxOverride("hover",ButtonStyle(new Color("344750"),new Color("7b8f99")));
        button.AddThemeStyleboxOverride("pressed",ButtonStyle(new Color("57452c"),new Color("a87e42")));
        return button;
    }

    private static StyleBoxFlat ButtonStyle(Color fill,Color border) => new()
    {
        BgColor=fill,BorderColor=border,BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,
        CornerRadiusBottomLeft=5,CornerRadiusBottomRight=5,CornerRadiusTopLeft=5,CornerRadiusTopRight=5,
        ContentMarginLeft=9,ContentMarginRight=9,ContentMarginTop=3,ContentMarginBottom=3
    };
}
