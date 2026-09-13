using Godot;
using System;
using System.Globalization;

namespace EstunStudio;

public partial class Main : Node3D
{
    private StudioWorld _world = null!;
    private RobotModel _robot = null!;
    private RobotController _controller = null!;
    private Camera3D _camera = null!;
    private Control _ui = null!;
    private Label _tcp = null!, _motion = null!, _fps = null!, _toast = null!, _program = null!;
    private Panel _help = null!, _programPanel = null!;
    private Button _qualityButton = null!;
    private ViewCube _viewCube=null!;
    private EffectsPanel _effectsPanel=null!;
    private WeldingWorkspace _welding=null!;
    private Font _font = null!;
    private Vector3 _target = new(.40f,.70f,-.10f);
    private float _yaw = 2.5f, _pitch = .23f, _distance = 3.8f;
    private bool _orbit, _pan, _cinema, _ultra=true;
    private double _time, _toastUntil;
    private string? _capturePath;
    private int _captureFrame;
    private bool _captureDone;
    private bool _autoPlan,_autoSimulate,_startedAutoPlan,_startedAutoSimulate;
    private double _autoSimTime;
    private readonly Color _ink=new("e6e9e8"), _muted=new("89969e"), _amber=new("f5b451");

    public override void _Ready()
    {
        GetWindow().Title="ESTUN Studio — S20-180 PRO";
        DisplayServer.SetIcon(GD.Load<Texture2D>("res://Assets/icon.svg").GetImage());
        _font=new FontVariation {BaseFont=GD.Load<Font>("res://Assets/Fonts/Inter-Regular.ttf"),VariationOpentype=new Godot.Collections.Dictionary { ["wght"]=450 }};
        _world=new StudioWorld {Name="Studio"};AddChild(_world);_world.Build();
        _robot=new RobotModel {Name="S180_PRO"};_world.RobotMount.AddChild(_robot);_robot.Build();
        var torch=new WeldTorch {Name="MIG MAG torch"};_robot.Joints[5].AddChild(torch);torch.Build();_robot.ToolTip.Transform=WeldTorch.ToolTransform;
        _controller=new RobotController {Name="Virtual controller"};AddChild(_controller);_controller.Initialize(_robot.Joints,_robot.ToolTip);
        _camera=new Camera3D {Name="Studio camera",Current=true,Fov=40,Near=.03f,Far=100,KeepAspect=Camera3D.KeepAspectEnum.Height};
        AddChild(_camera);
        _camera.Attributes=new CameraAttributesPractical {DofBlurFarEnabled=false,DofBlurNearEnabled=false,DofBlurAmount=0};
        BuildInterface();
        UpdateCamera();
        _effectsPanel.ApplySettings(false);
        string[] args=OS.GetCmdlineUserArgs();
        for(int i=0;i<args.Length;i++)
        {
            if(args[i]=="--capture" && i+1<args.Length) _capturePath=args[++i];
            if(args[i]=="--balanced") { _ultra=false; _world.SetUltra(false); _qualityButton.Text="BALANCED"; }
            if(args[i]=="--sample") _welding.CallDeferred(nameof(WeldingWorkspace.LoadSample));
            if(args[i]=="--plan")_autoPlan=true;
            if(args[i]=="--simulate"){_autoPlan=true;_autoSimulate=true;}
        }
        ShowToast("Ready · Enable DRIVES, then jog or start a program. SPACE stops motion.",8);
    }

    private void BuildInterface()
    {
        var canvas=new CanvasLayer {Name="Interface"};AddChild(canvas);
        _ui=new Control {Name="Workspace",MouseFilter=Control.MouseFilterEnum.Ignore};canvas.AddChild(_ui);_ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var theme=new Theme {DefaultFont=_font,DefaultFontSize=14};_ui.Theme=theme;
        var vignette=new ColorRect {MouseFilter=Control.MouseFilterEnum.Ignore};_ui.AddChild(vignette);vignette.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        vignette.Material=new ShaderMaterial {Shader=new Shader {Code="shader_type canvas_item; void fragment(){ float edge=1.0-smoothstep(0.0,0.48,UV.x); float top=1.0-smoothstep(0.35,0.70,UV.y); COLOR=vec4(0.028,0.042,0.052,edge*top*0.78); }"}};
        var top=PanelAt(_ui,0,0,1600,82,new Color("171d23"));top.MouseFilter=Control.MouseFilterEnum.Stop;
        top.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);top.OffsetBottom=82;
        LabelAt(top,"ESTUN",32,17,150,39,28,_ink);
        LabelAt(top,"STUDIO",172,26,95,22,12,_muted);
        PanelAt(top,283,23,1,34,new Color("39434a"));
        LabelAt(top,"WELDING WORKSPACE",308,19,270,23,13,_ink);
        LabelAt(top,"S20-180 PRO   /   CAD TO WELD",308,46,400,18,10,_muted);
        var sim=ButtonAt(top,"●  OFFLINE SIMULATION",-360,24,206,32,()=>ToggleHelp(),false);
        sim.AnchorLeft=sim.AnchorRight=1;
        sim.TooltipText="Offline robot welding simulation. Open operating guide.";
        var help=ButtonAt(top,"?",-132,24,38,32,ToggleHelp,false);help.AnchorLeft=help.AnchorRight=1;
        var fs=ButtonAt(top,"⛶",-80,24,44,32,ToggleFullscreen,false);fs.AnchorLeft=fs.AnchorRight=1;fs.TooltipText="Toggle fullscreen · F11";

        LabelAt(_ui,"01  /  ROBOTIC WELDING",35,113,500,19,10,_amber);
        LabelAt(_ui,"From CAD.\nTo a perfect seam.",31,143,730,105,38,_ink);
        LabelAt(_ui,"Import. Position. Review. Simulate.",35,261,520,22,13,_muted);

        _viewCube=new ViewCube {Position=new Vector2(948,112)};_ui.AddChild(_viewCube);_viewCube.Build();
        _viewCube.ViewRequested+=direction=>{_yaw=Mathf.Atan2(direction.X,direction.Z);_pitch=Mathf.Clamp(Mathf.Asin(direction.Y),-Mathf.Pi/2+.001f,Mathf.Pi/2-.001f);UpdateCamera();};
        _viewCube.HomeRequested+=()=>SetView(0);
        _viewCube.OrbitRequested+=delta=>{_yaw-=delta.X*.006f;_pitch=Mathf.Clamp(_pitch+delta.Y*.005f,-1.56f,1.56f);UpdateCamera();};

        var details=PanelAt(_ui,849,643,240,131,new Color(.055f,.08f,.1f,.72f));
        LabelAt(details,"LIVE TELEMETRY",17,13,256,19,10,_muted);
        _motion=LabelAt(details,"●  STANDSTILL",17,39,263,21,14,_ink);
        _tcp=LabelAt(details,"",17,69,214,58,11,new Color("b6c6ce"));

        var toolbar=PanelAt(_ui,360,885,725,45,new Color(.07f,.095f,.12f,.88f));toolbar.MouseFilter=Control.MouseFilterEnum.Stop;
        var axes=ButtonAt(toolbar,"TCP AXES",7,7,111,31,()=>{},false);
        axes.ToggleMode=true;axes.ButtonPressed=true;axes.Toggled+=(on)=>_world.ShowCoordinates=on;
        var trail=ButtonAt(toolbar,"TRACE PATH",124,7,119,31,()=>{},false);
        trail.ToggleMode=true;trail.Toggled+=(on)=>{_world.ShowTrail=on;ShowToast(on?"TCP path recording enabled":"TCP path cleared");};
        var quality=ButtonAt(toolbar,"EFFECTS",249,7,105,31,()=>{},false);
        _qualityButton=quality;
        quality.Pressed+=()=>_effectsPanel.Toggle();
        ButtonAt(toolbar,"WELD / CAD",360,7,118,31,()=>_welding.Toggle(),false);
        ButtonAt(toolbar,"CINEMA",484,7,102,31,ToggleCinema,false).TooltipText="Hide interface and orbit camera · Tab";
        ButtonAt(toolbar,"CAPTURE",592,7,125,31,SaveCapture,false).TooltipText="Save PNG to Pictures / ESTUN Studio · F12";

        var pendant=new PendantPanel();_ui.AddChild(pendant);pendant.Build(_controller);pendant.Toast+=s=>ShowToast(s);pendant.HomeViewRequested+=()=>SetView(0);
        var footer=PanelAt(_ui,0,952,1600,48,new Color("171d23"));footer.MouseFilter=Control.MouseFilterEnum.Stop;
        footer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);footer.OffsetTop=-48;
        LabelAt(footer,"LMB / RMB  ORBIT     WHEEL  ZOOM     MMB  PAN",32,15,570,18,10,_muted);
        LabelAt(footer,"SPACE  STOP     ESC  E-STOP     TAB  CINEMA",530,15,520,18,10,_muted);
        _fps=LabelAt(footer,"FORWARD+  /  ULTRA",-265,15,230,18,10,_muted);_fps.AnchorLeft=_fps.AnchorRight=1;_fps.HorizontalAlignment=HorizontalAlignment.Right;
        _toast=LabelAt(_ui,"",350,828,720,41,12,new Color("d6e3df"));
        _toast.HorizontalAlignment=HorizontalAlignment.Center;
        _toast.AddThemeColorOverride("font_shadow_color",new Color(0,0,0,.9f));_toast.AddThemeConstantOverride("shadow_offset_y",1);_toast.AddThemeConstantOverride("shadow_outline_size",3);
        BuildHelp();BuildProgramPanel();
        _welding=new WeldingWorkspace {Name="Welding"};AddChild(_welding);_welding.Build(this,_ui,_robot,_controller,_camera);_welding.Toast+=s=>ShowToast(s);
        _welding.FocusRequested+=p=>{_target=new Vector3(.52f,.66f,0);_distance=3.6f;UpdateCamera();};
        _effectsPanel=new EffectsPanel();_ui.AddChild(_effectsPanel);_effectsPanel.SettingsChanged+=s=>{_welding.Effects.SparksEnabled=s.Sparks;_welding.Effects.SmokeEnabled=s.Smoke;_welding.Effects.BeadDetail=s.BeadDetail;};_effectsPanel.Build(_world,_camera);
    }

    private void BuildHelp()
    {
        _help=PanelAt(_ui,340,250,740,550,new Color("202a30"));_help.Visible=false;_help.MouseFilter=Control.MouseFilterEnum.Stop;
        LabelAt(_help,"WORKSPACE GUIDE",30,24,590,31,23,_ink);
        ButtonAt(_help,"×",670,20,41,36,ToggleHelp,false);
        LabelAt(_help,"MOVE THE ROBOT",30,85,650,23,12,_amber);
        LabelAt(_help,"1   Click DRIVES on the pendant.\n2   Hold an axis − / + key, or select a row and use ← / →.\n3   Click HOME, RUN or SIMULATE to start automatic motion.\n4   SPACE or STOP MOTION stops. ESC latches emergency stop.",30,120,675,117,15,new Color("bdcbd2"));
        LabelAt(_help,"JOINT / WORLD / TOOL",30,253,650,23,12,_amber);
        LabelAt(_help,"JOINT rotates a single motor. WORLD moves the TCP in the base frame.\nTOOL moves along the tool's own axes. A/B/C rotate about X/Y/Z.\nTCP coordinates use a right-handed Y-up frame; distances are millimeters.",30,287,680,85,14,new Color("bdcbd2"));
        LabelAt(_help,"TEACH & REPLAY",30,384,650,23,12,_amber);
        LabelAt(_help,"REC stores a pose. Click RUN to replay taught points with DRIVES on.\nSPACE stops movement; use RESET after an E-stop.\nThis is an offline visual simulator with source-derived robot geometry.",30,419,680,95,14,new Color("bdcbd2"));
    }
    private void BuildProgramPanel()
    {
        _programPanel=PanelAt(_ui,660,448,425,366,new Color("202a30"));_programPanel.Visible=false;_programPanel.MouseFilter=Control.MouseFilterEnum.Stop;
        LabelAt(_programPanel,"TAUGHT POSITIONS",20,19,328,25,17,_ink);
        ButtonAt(_programPanel,"×",365,15,36,31,()=>_programPanel.Hide(),false);
        _program=LabelAt(_programPanel,"No points recorded.",20,60,383,200,13,new Color("bdcbd2"));
        ButtonAt(_programPanel,"SAVE",20,276,113,36,()=>{_controller.SaveWaypoints();ShowToast(_controller.Status);},false);
        ButtonAt(_programPanel,"LOAD",150,276,113,36,()=>{_controller.LoadWaypoints();UpdateProgram();ShowToast(_controller.Status);},false);
        ButtonAt(_programPanel,"CLEAR",280,276,123,36,()=>{_controller.ClearWaypoints();UpdateProgram();ShowToast("Program cleared from working memory");},false);
        LabelAt(_programPanel,"Local storage  /  S20-180 Pro program",20,328,393,18,10,_muted);
        _controller.WaypointsChanged+=UpdateProgram;
    }
    private void UpdateProgram()
    {
        if(_program==null) return;
        if(_controller.Waypoints.Count==0){_program.Text="No points recorded.\n\nMove the robot, then press REC on the pendant.\nA sequence can contain up to 100 positions.";return;}
        string s="";int count=0;
        foreach(var p in _controller.Waypoints)
        {
            if(count++>=7){s+=$"\n+ {_controller.Waypoints.Count-7} more positions";break;}
            s+=$"{p.Name}    ·    Joint motion    ·    {p.SpeedOverride*100:0}%\n";
        }
        _program.Text=s;
    }

    public override void _Process(double delta)
    {
        _time+=delta;
        if(_cinema) { _yaw+=(float)delta*.10f;UpdateCamera(); }
        if(_controller==null) return;
        _world.UpdateTcp(_robot.ToolTip.GlobalTransform,_controller.MotionActive);
        _viewCube.SetCameraBasis(_camera.GlobalBasis);
        var p=_controller.TcpPosition*1000;
        _tcp.Text=string.Format(CultureInfo.InvariantCulture,"TCP / BASE                       mm\nX  {0,8:0.0}   Y  {1,8:0.0}\nZ  {2,8:0.0}",p.X,p.Y,p.Z);
        _motion.Text="●  "+(_controller.IsPlannedMotion?(_welding.Effects.IsArcActive?"WELDING ARC ACTIVE":"TRAVEL MOVE"):_controller.ActiveMotion.ToUpperInvariant());
        _motion.Modulate=_controller.EmergencyStopped?new Color("ff7770"):_controller.MotionActive?new Color("88d3b4"):Colors.White;
        _fps.Text=$"{Engine.GetFramesPerSecond()} FPS   /   FORWARD+   /   C#";
        _toast.Visible=_time<_toastUntil;
        if(_autoPlan && !_startedAutoPlan && _welding.Part!=null && !_welding.IsBusy)
        {_startedAutoPlan=true;_welding.Generate();}
        if(_autoSimulate && _startedAutoPlan && !_startedAutoSimulate && !_welding.IsBusy && _welding.Program?.ReadyCount>0)
        {_startedAutoSimulate=true;_controller.SetDrives(true);_welding.Run();}
        if(_startedAutoSimulate)_autoSimTime+=delta;
        bool simulationCaptureReady=!_autoSimulate || (_autoSimTime>1 && _welding.Effects.IsArcActive && _welding.Effects.BeadCount>24) || (_startedAutoSimulate&&!_welding.IsSimulating) || (_startedAutoPlan&&_welding.Program?.ReadyCount==0);
        if(_capturePath!=null && !_captureDone && ++_captureFrame>180 && !_welding.IsBusy && simulationCaptureReady)
        {
            _captureDone=true;CallDeferred(nameof(CaptureAndQuit));
        }
    }
    private async void CaptureAndQuit()
    {
        await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
        var path=_capturePath!;
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print("CAPTURE SAVED: "+path);GetTree().Quit();
    }
    private async void SaveCapture()
    {
        string dir=System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures),"ESTUN Studio");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            string path=System.IO.Path.Combine(dir,$"ESTUN-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
            Error result=GetViewport().GetTexture().GetImage().SavePng(path);
            ShowToast(result==Error.Ok?"Capture saved to Pictures / ESTUN Studio":"Capture failed: "+result);
        }
        catch(Exception ex){ShowToast("Capture failed: "+ex.Message);}
    }
    public override void _UnhandledInput(InputEvent e)
    {
        if(e is InputEventMouseButton m)
        {
            if(m.ButtonIndex==MouseButton.Left || m.ButtonIndex==MouseButton.Right) _orbit=m.Pressed;
            if(m.ButtonIndex==MouseButton.Middle) _pan=m.Pressed;
            if(m.Pressed && m.ButtonIndex==MouseButton.WheelUp){_distance=Mathf.Max(2.0f,_distance*.91f);UpdateCamera();}
            if(m.Pressed && m.ButtonIndex==MouseButton.WheelDown){_distance=Mathf.Min(15,_distance*1.10f);UpdateCamera();}
        }
        if(e is InputEventMouseMotion motion)
        {
            if(_orbit) { _yaw-=motion.Relative.X*.006f;_pitch=Mathf.Clamp(_pitch+motion.Relative.Y*.005f,-1.56f,1.56f);UpdateCamera(); }
            if(_pan) { _target+=(-_camera.GlobalBasis.X*motion.Relative.X+_camera.GlobalBasis.Y*motion.Relative.Y)*_distance*.0012f;UpdateCamera(); }
        }
        if(e is InputEventKey k && k.Pressed && !k.Echo)
        {
            switch(k.Keycode)
            {
                case Key.Key1:SetView(0);break;case Key.Key2:SetView(1);break;case Key.Key3:SetView(2);break;case Key.Key4:SetView(3);break;
                case Key.F:SetView(0);break;case Key.Tab:ToggleCinema();break;case Key.F1:ToggleHelp();break;case Key.F11:ToggleFullscreen();break;case Key.F12:SaveCapture();break;
            }
        }
    }
    public override void _Input(InputEvent e)
    {
        if(e is InputEventMouseButton m && !m.Pressed){if(m.ButtonIndex==MouseButton.Left || m.ButtonIndex==MouseButton.Right)_orbit=false;if(m.ButtonIndex==MouseButton.Middle)_pan=false;}
    }
    public override void _Notification(int what)
    {
        if(what==NotificationApplicationFocusOut){_orbit=_pan=false;_controller?.StopMotion();}
    }
    private void UpdateCamera()
    {
        Vector3 offset=new(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch));
        _camera.Position=_target+offset*_distance;
        _camera.LookAt(_target,Vector3.Up);
        // Offset lens to reserve the right third of the screen for the pendant.
        _camera.HOffset=_cinema?0:_distance*.135f;
    }
    private void SetView(int view)
    {
        _target=new Vector3(.40f,.70f,-.10f);_distance=3.8f;
        (_yaw,_pitch)=view switch {1=>(0,.08f),2=>(Mathf.Pi/2,.10f),3=>(0,1.46f),_=>(2.5f,.23f)};
        UpdateCamera();
    }
    private void ToggleCinema()
    {
        _cinema=!_cinema;_ui.Visible=!_cinema;
        _welding.Stop();
        _controller.StopMotion();
        UpdateCamera();
    }
    private void ToggleHelp(){_help.Visible=!_help.Visible;_controller.StopMotion();}
    private void ToggleFullscreen()=>DisplayServer.WindowSetMode(DisplayServer.WindowGetMode()==DisplayServer.WindowMode.Fullscreen?DisplayServer.WindowMode.Windowed:DisplayServer.WindowMode.Fullscreen);
    private void ShowToast(string message,double duration=4.5){_toast.Text=message;_toastUntil=_time+duration;}
    private Panel PanelAt(Control parent,float x,float y,float w,float h,Color color)
    {
        var p=new Panel {Position=new Vector2(x,y),Size=new Vector2(w,h),MouseFilter=Control.MouseFilterEnum.Ignore};
        p.AddThemeStyleboxOverride("panel",Flat(color,7));parent.AddChild(p);return p;
    }
    private Label LabelAt(Control parent,string text,float x,float y,float w,float h,int fontSize,Color color)
    {
        var l=new Label {Text=text,Position=new Vector2(x,y),Size=new Vector2(w,h),MouseFilter=Control.MouseFilterEnum.Ignore};
        l.AddThemeColorOverride("font_color",color);l.AddThemeFontSizeOverride("font_size",fontSize);parent.AddChild(l);return l;
    }
    private Button ButtonAt(Control parent,string text,float x,float y,float w,float h,Action action,bool primary)
    {
        var b=new Button {Text=text,Position=new Vector2(x,y),Size=new Vector2(w,h),FocusMode=Control.FocusModeEnum.None,MouseDefaultCursorShape=Control.CursorShape.PointingHand};
        b.AddThemeStyleboxOverride("normal",Flat(primary?_amber:new Color("283139"),5));
        b.AddThemeStyleboxOverride("hover",Flat(new Color("3b4952"),5));
        b.AddThemeStyleboxOverride("pressed",Flat(new Color("465c60"),5));
        b.AddThemeColorOverride("font_color",_ink);b.AddThemeFontSizeOverride("font_size",11);parent.AddChild(b);b.Pressed+=action;return b;
    }
    private static StyleBoxFlat Flat(Color color,int radius)=>new(){BgColor=color,CornerRadiusBottomLeft=radius,CornerRadiusBottomRight=radius,CornerRadiusTopLeft=radius,CornerRadiusTopRight=radius};
}
