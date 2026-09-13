using Godot;
using System;
using System.Linq;
using System.Globalization;

namespace EstunStudio;

public partial class Main : Node3D
{
    private StudioWorld _world=null!;
    private RobotModel _robot=null!;
    private RobotController _controller=null!;
    private Camera3D _camera=null!;
    private Control _ui=null!;
    private ViewCube _viewCube=null!;
    private EffectsPanel _effectsPanel=null!;
    private WeldingWorkspace _welding=null!;
    private PendantPanel _pendant=null!;
    private ScrollContainer _pendantDock=null!;
    private PanelContainer _top=null!,_footer=null!,_viewTools=null!,_telemetry=null!,_toastPanel=null!,_programPanel=null!,_help=null!;
    private Label _tcp=null!,_motion=null!,_fps=null!,_toast=null!,_program=null!,_status=null!;
    private Button _pendantToggle=null!,_cadToggle=null!;
    private Vector3 _target=new(.40f,.70f,-.10f);
    private float _yaw=2.35f,_pitch=.22f,_distance=3.25f;
    private bool _orbit,_pan,_cinema,_layoutReady;
    private double _time,_toastUntil,_readoutElapsed;
    private string? _capturePath;
    private int _captureFrame;
    private bool _captureDone,_autoPlan,_autoSimulate,_startedAutoPlan,_startedAutoSimulate;
    private double _autoSimTime;
    public bool PendantVisible=>_pendantDock.Visible;

    public override void _Ready()
    {
        GetWindow().Title="ESTUN Studio · S20-180 Pro";
        GetWindow().ContentScaleMode=Window.ContentScaleModeEnum.Disabled;
        GetWindow().ContentScaleFactor=1;
        GetViewport().Snap2DTransformsToPixel=true;
        DisplayServer.SetIcon(GD.Load<Texture2D>("res://Assets/icon.svg").GetImage());
        _world=new StudioWorld {Name="Studio"};AddChild(_world);_world.Build();
        _robot=new RobotModel();_world.RobotMount.AddChild(_robot);_robot.Build();
        var torch=new WeldTorch {Name="MIG MAG torch"};_robot.Joints[5].AddChild(torch);torch.Build();_robot.ToolTip.Transform=WeldTorch.ToolTransform;
        _controller=new RobotController {Name="Virtual controller"};AddChild(_controller);_controller.Initialize(_robot.Joints,_robot.ToolTip);
        _camera=new Camera3D {Name="Studio camera",Current=true,Fov=38,Near=.06f,Far=50,KeepAspect=Camera3D.KeepAspectEnum.Height};AddChild(_camera);
        _camera.Attributes=new CameraAttributesPractical {DofBlurFarEnabled=false,DofBlurNearEnabled=false,DofBlurAmount=0};
        BuildInterface();_layoutReady=true;LayoutInterface();UpdateCamera();_effectsPanel.ApplySettings(false);
        GetViewport().SizeChanged+=LayoutInterface;
        string[] args=OS.GetCmdlineUserArgs();
        for(int i=0;i<args.Length;i++)
        {
            if(args[i]=="--capture"&&i+1<args.Length)_capturePath=args[++i];
            if(args[i]=="--balanced"){_effectsPanel.Settings.ResolutionMode=RenderResolution.Auto;_effectsPanel.ApplySettings(false);}
            if(args[i]=="--sample")_welding.CallDeferred(nameof(WeldingWorkspace.LoadSample));
            if(args[i]=="--plan")_autoPlan=true;
            if(args[i]=="--simulate"){_autoPlan=true;_autoSimulate=true;}
            if(args[i]=="--hide-pendant")SetPendantVisible(false);
            if(args[i]=="--no-shadows"){_effectsPanel.Settings.Shadows=false;_effectsPanel.ApplySettings(false);}
            if(args[i]=="--no-ao"){_effectsPanel.Settings.Ssao=false;_effectsPanel.ApplySettings(false);}
            if(args[i]=="--closeup"){_target=new Vector3(0,.37f,0);_distance=1.5f;UpdateCamera();}
            if(args[i]=="--display")_effectsPanel.Show();
        }
        ShowToast("Ready. Import a STEP file or load the sample.");
    }

    private void BuildInterface()
    {
        var layer=new CanvasLayer {Name="Interface"};AddChild(layer);
        _ui=new Control {Name="Workspace",MouseFilter=Control.MouseFilterEnum.Ignore,Theme=StudioTheme.Create()};layer.AddChild(_ui);_ui.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _top=Panel(_ui,StudioTheme.Background);_top.Name="TopBar";
        var topRow=Row(Inset(_top,18,9));
        var brand=Label("ESTUN",19);topRow.AddChild(brand);topRow.AddChild(Label("/  Weld Studio",14,StudioTheme.Muted));
        topRow.AddChild(new Control{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});
        _cadToggle=Button(topRow,"Workpiece",()=>_welding.Toggle());_cadToggle.Name="ToggleWorkpiece";
        _pendantToggle=Button(topRow,"Pendant",()=>SetPendantVisible(!PendantVisible));_pendantToggle.Name="TogglePendant";
        Button(topRow,"Program",()=>{_programPanel.Visible=!_programPanel.Visible;UpdateProgram();LayoutInterface();});
        Button(topRow,"Display",()=>{_effectsPanel.Toggle();LayoutInterface();});
        Button(topRow,"Help",ToggleHelp);Button(topRow,"Full screen",ToggleFullscreen).TooltipText="F11";

        _welding=new WeldingWorkspace {Name="Welding"};AddChild(_welding);_welding.Build(this,_ui,_robot,_controller,_camera);
        _welding.Toast+=s=>ShowToast(s);_welding.PanelVisibilityChanged+=LayoutInterface;
        _welding.FocusRequested+=_=>{_target=new(.52f,.66f,0);_distance=3.4f;LayoutInterface();UpdateCamera();};
        _pendantDock=new ScrollContainer {Name="PendantDock",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,MouseFilter=Control.MouseFilterEnum.Stop};_ui.AddChild(_pendantDock);
        _pendant=new PendantPanel {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};_pendantDock.AddChild(_pendant);_pendant.Build(_controller);
        _pendant.Toast+=s=>ShowToast(s);_pendant.CloseRequested+=()=>SetPendantVisible(false);_pendant.HomeViewRequested+=()=>SetView(0);

        _viewCube=new ViewCube();_ui.AddChild(_viewCube);_viewCube.Build();
        _viewCube.ViewRequested+=d=>{_yaw=Mathf.Atan2(d.X,d.Z);_pitch=Mathf.Clamp(Mathf.Asin(d.Y),-1.569f,1.569f);UpdateCamera();};
        _viewCube.HomeRequested+=()=>SetView(0);_viewCube.OrbitRequested+=d=>{_yaw-=d.X*.006f;_pitch=Mathf.Clamp(_pitch+d.Y*.005f,-1.56f,1.56f);UpdateCamera();};
        _viewTools=Panel(_ui,new Color("20272e"));var tools=Row(Inset(_viewTools,6,5));
        Button(tools,"Fit",()=>SetView(0)).TooltipText="F · Fit the working cell";
        var axes=Button(tools,"TCP",()=>{});axes.ToggleMode=true;axes.ButtonPressed=true;axes.Toggled+=on=>_world.ShowCoordinates=on;
        var trace=Button(tools,"Trace",()=>{});trace.ToggleMode=true;trace.Toggled+=on=>_world.ShowTrail=on;
        Button(tools,"Capture",SaveCapture).TooltipText="F12 · Save a PNG";Button(tools,"Cinema",ToggleCinema).TooltipText="Tab · Hide workspace controls";
        _telemetry=Panel(_ui,new Color(.10f,.13f,.16f,.90f));var telemetry=Column(Inset(_telemetry,12,8),3);
        _motion=Label("STANDSTILL",12,StudioTheme.Muted);telemetry.AddChild(_motion);_tcp=Label("",13);telemetry.AddChild(_tcp);

        _footer=Panel(_ui,StudioTheme.Background);var footer=Row(Inset(_footer,16,4));
        _status=Label("Ready",12,StudioTheme.Muted);_status.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;_status.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;footer.AddChild(_status);
        _fps=Label("",12,StudioTheme.Muted);footer.AddChild(_fps);
        _toastPanel=Panel(_ui,new Color("293841"));_toast=Label("",13);_toast.HorizontalAlignment=HorizontalAlignment.Center;Inset(_toastPanel,14,9).AddChild(_toast);
        BuildHelp();BuildProgramPanel();
        _effectsPanel=new EffectsPanel();_ui.AddChild(_effectsPanel);_effectsPanel.SettingsChanged+=s=>{_welding.Effects.SparksEnabled=s.Sparks;_welding.Effects.SmokeEnabled=s.Smoke;_welding.Effects.BeadDetail=s.BeadDetail;};_effectsPanel.Build(_world,_camera);
    }

    public void SetPendantVisible(bool visible)
    {
        if(_pendantDock==null)return;
        if(_pendantDock.Visible!=visible){_welding.Stop();_controller.StopMotion();}
        _pendantDock.Visible=visible;LayoutInterface();
    }

    private void LayoutInterface()
    {
        if(!_layoutReady)return;
        Vector2 size=GetViewport().GetVisibleRect().Size;float w=Mathf.Floor(size.X),h=Mathf.Floor(size.Y);
        SetRect(_top,0,0,w,56);SetRect(_footer,0,h-32,w,32);
        float left=_welding.EditingVisible&&!_cinema?344:12;
        float right=PendantVisible&&!_cinema?w-352:w-12;
        SetRect(_welding.Panel,12,68,320,h-112);
        SetRect(_pendantDock,w-340,68,328,h-112);
        _pendantToggle.Text=PendantVisible?"Hide pendant":"Show pendant";
        _cadToggle.Text=_welding.EditingVisible?"Hide workpiece":"Workpiece";
        _viewCube.Position=new Vector2(right-146,70);
        Vector2 toolSize=_viewTools.GetCombinedMinimumSize();float tw=Mathf.Max(toolSize.X,376);
        SetRect(_viewTools,Mathf.Round((left+right-tw)*.5f),h-86,tw,44);
        SetRect(_telemetry,left+10,78,250,62);
        SetRect(_toastPanel,Mathf.Round((left+right-520)*.5f),h-140,520,40);
        PositionModal(_help,w,h,620,460);PositionModal(_programPanel,w,h,480,410);
        if(_effectsPanel!=null){_effectsPanel.Position=new Vector2(Mathf.Max(12,w-_effectsPanel.Size.X-24),68);}
        _camera.HOffset=_cinema?0:-((left+right)*.5f-w*.5f)*_distance*2*Mathf.Tan(Mathf.DegToRad(_camera.Fov*.5f))/h;
    }
    private static void PositionModal(Control panel,float w,float h,float width,float height)=>SetRect(panel,Mathf.Round((w-width)*.5f),Mathf.Round((h-height)*.5f),width,height);
    private static void SetRect(Control control,float x,float y,float width,float height){control.Position=new Vector2(x,y);control.Size=new Vector2(width,Mathf.Max(1,height));}

    private void BuildHelp()
    {
        _help=Panel(_ui,StudioTheme.Panel);_help.ZIndex=30;_help.Hide();var col=Column(Inset(_help,22,18),16);
        var head=Row(col);head.AddChild(Label("Workspace guide",20));head.AddChild(new Control{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});Button(head,"Close",ToggleHelp);
        foreach(var item in new[]{("Prepare","Import STEP, position with W / E, choose the seam angle range and exclude incorrect candidates. Generate a program before simulation."),("Operate","Enable DRIVES. Jog with the axis buttons or arrow keys; click HOME, RUN or SIMULATE for automatic motion. Space stops motion. Escape latches emergency stop."),("Navigate","Drag to orbit, middle-drag to pan and wheel to zoom. The cube selects views. P hides the pendant, F fits the cell, F11 is fullscreen, Tab is cinema."),("Image quality","Display shows the actual 3D buffer resolution. Native renders 100%. Temporal AA and depth of field are off in the sharp preset.")})
        {col.AddChild(Label(item.Item1,14,StudioTheme.Accent));var body=Label(item.Item2,14);body.AutowrapMode=TextServer.AutowrapMode.WordSmart;col.AddChild(body);}
    }
    private void BuildProgramPanel()
    {
        _programPanel=Panel(_ui,StudioTheme.Panel);_programPanel.ZIndex=30;_programPanel.Hide();var col=Column(Inset(_programPanel,20,18),16);
        var head=Row(col);head.AddChild(Label("Taught positions",20));head.AddChild(new Control{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill});Button(head,"Close",_programPanel.Hide);
        _program=Label("No points recorded.",14);_program.SizeFlagsVertical=Control.SizeFlags.ExpandFill;col.AddChild(_program);
        var row=Row(col);Button(row,"Save",()=>{_controller.SaveWaypoints();ShowToast(_controller.Status);});Button(row,"Load",()=>{_controller.LoadWaypoints();UpdateProgram();ShowToast(_controller.Status);});Button(row,"Clear",()=>{_controller.ClearWaypoints();UpdateProgram();});
        _controller.WaypointsChanged+=UpdateProgram;
    }
    private void UpdateProgram()
    {
        if(_program==null)return;
        _program.Text=_controller.Waypoints.Count==0?"No points recorded. Use RECORD on the pendant.":string.Join("\n",_controller.Waypoints.Take(9).Select(p=>$"{p.Name}     Joint motion     {p.SpeedOverride*100:0}%"));
        if(_controller.Waypoints.Count>9)_program.Text+=$"\n+ {_controller.Waypoints.Count-9} more positions";
    }

    public override void _Process(double delta)
    {
        _time+=delta;if(_cinema){_yaw+=(float)delta*.10f;UpdateCamera();}
        if(_controller==null||!_layoutReady)return;
        _world.UpdateTcp(_robot.ToolTip.GlobalTransform,_controller.MotionActive);_viewCube.SetCameraBasis(_camera.GlobalBasis);
        _readoutElapsed+=delta;
        if(_readoutElapsed>=.1)
        {
            _readoutElapsed=0;Vector3 p=_controller.TcpPosition*1000;
            _tcp.Text=string.Format(CultureInfo.InvariantCulture,"X {0:0.0}    Y {1:0.0}    Z {2:0.0}",p.X,p.Y,p.Z);
            _motion.Text=_controller.EmergencyStopped?"EMERGENCY STOP":_controller.IsPlannedMotion?(_welding.Effects.IsArcActive?"WELDING":"TRAVEL"):_controller.ActiveMotion.ToUpperInvariant();
            _motion.Modulate=_controller.EmergencyStopped?new Color("ee7874"):_controller.MotionActive?new Color("89d1ad"):Colors.White;
            Vector2I render=RenderResolution.GetRenderSize(GetViewport());float scale=GetViewport().Scaling3DScale;
            _fps.Text=$"{Engine.GetFramesPerSecond()} FPS  ·  3D {Mathf.RoundToInt(render.X*scale)} × {Mathf.RoundToInt(render.Y*scale)}  {scale*100:0}%";
            _status.Text=_welding.IsBusy?_welding.ProgressText:!string.IsNullOrWhiteSpace(_welding.StatusText)?_welding.StatusText:"S20-180 Pro  ·  Offline  ·  Space stops motion";
        }
        _toastPanel.Visible=_time<_toastUntil;
        if(_autoPlan&&!_startedAutoPlan&&_welding.Part!=null&&!_welding.IsBusy){_startedAutoPlan=true;_welding.Generate();}
        if(_autoSimulate&&_startedAutoPlan&&!_startedAutoSimulate&&!_welding.IsBusy&&_welding.Program?.ReadyCount>0){_startedAutoSimulate=true;_controller.SetDrives(true);_welding.Run();}
        if(_startedAutoSimulate)_autoSimTime+=delta;
        bool simulationReady=!_autoSimulate||(_autoSimTime>1&&_welding.Effects.IsArcActive&&_welding.Effects.BeadCount>24)||(_startedAutoSimulate&&!_welding.IsSimulating)||(_startedAutoPlan&&_welding.Program?.ReadyCount==0);
        if(_capturePath!=null&&!_captureDone&&++_captureFrame>180&&!_welding.IsBusy&&simulationReady){_captureDone=true;CallDeferred(nameof(CaptureAndQuit));}
    }
    private async void CaptureAndQuit(){await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);GetViewport().GetTexture().GetImage().SavePng(_capturePath!);GD.Print("CAPTURE SAVED: "+_capturePath);GetTree().Quit();}
    private async void SaveCapture()
    {
        try{string dir=System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures),"ESTUN Studio");System.IO.Directory.CreateDirectory(dir);string path=System.IO.Path.Combine(dir,$"ESTUN-{DateTime.Now:yyyyMMdd-HHmmss}.png");await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);Error result=GetViewport().GetTexture().GetImage().SavePng(path);ShowToast(result==Error.Ok?"Capture saved to Pictures / ESTUN Studio":"Capture failed: "+result);}catch(Exception e){ShowToast(e.Message);}
    }
    public override void _UnhandledInput(InputEvent e)
    {
        if(e is InputEventMouseButton m)
        {
            if(m.ButtonIndex==MouseButton.Left||m.ButtonIndex==MouseButton.Right)_orbit=m.Pressed;
            if(m.ButtonIndex==MouseButton.Middle)_pan=m.Pressed;
            if(m.Pressed&&m.ButtonIndex==MouseButton.WheelUp){_distance=Mathf.Max(1.5f,_distance*.91f);UpdateCamera();}
            if(m.Pressed&&m.ButtonIndex==MouseButton.WheelDown){_distance=Mathf.Min(15,_distance*1.10f);UpdateCamera();}
        }
        if(e is InputEventMouseMotion motion)
        {
            if(_orbit){_yaw-=motion.Relative.X*.006f;_pitch=Mathf.Clamp(_pitch+motion.Relative.Y*.005f,-1.56f,1.56f);UpdateCamera();}
            if(_pan){_target+=(-_camera.GlobalBasis.X*motion.Relative.X+_camera.GlobalBasis.Y*motion.Relative.Y)*_distance*.0012f;UpdateCamera();}
        }
        if(e is InputEventKey {Pressed:true,Echo:false} k)switch(k.Keycode)
        {
            case Key.Key1:SetView(0);break;case Key.Key2:SetView(1);break;case Key.Key3:SetView(2);break;case Key.Key4:SetView(3);break;
            case Key.P:SetPendantVisible(!PendantVisible);break;case Key.F:SetView(0);break;case Key.Tab:ToggleCinema();break;case Key.F1:ToggleHelp();break;case Key.F11:ToggleFullscreen();break;case Key.F12:SaveCapture();break;
        }
    }
    public override void _Input(InputEvent e){if(e is InputEventMouseButton m&&!m.Pressed){if(m.ButtonIndex==MouseButton.Left||m.ButtonIndex==MouseButton.Right)_orbit=false;if(m.ButtonIndex==MouseButton.Middle)_pan=false;}}
    public override void _Notification(int what){if(what==NotificationApplicationFocusOut){_orbit=_pan=false;_controller?.StopMotion();}}
    private void UpdateCamera()
    {
        _camera.Position=_target+new Vector3(Mathf.Sin(_yaw)*Mathf.Cos(_pitch),Mathf.Sin(_pitch),Mathf.Cos(_yaw)*Mathf.Cos(_pitch))*_distance;
        _camera.LookAt(_target,Vector3.Up);LayoutInterface();
    }
    private void SetView(int view){_target=new(.40f,.70f,-.10f);_distance=3.25f;(_yaw,_pitch)=view switch{1=>(0,.08f),2=>(Mathf.Pi/2,.10f),3=>(0,1.46f),_=>(2.35f,.22f)};UpdateCamera();}
    private void ToggleCinema(){_cinema=!_cinema;_ui.Visible=!_cinema;_welding.Stop();_controller.StopMotion();UpdateCamera();}
    private void ToggleHelp(){_help.Visible=!_help.Visible;_controller.StopMotion();LayoutInterface();}
    private void ToggleFullscreen()=>DisplayServer.WindowSetMode(DisplayServer.WindowGetMode()==DisplayServer.WindowMode.Fullscreen?DisplayServer.WindowMode.Windowed:DisplayServer.WindowMode.Fullscreen);
    private void ShowToast(string message,double duration=4){_toast.Text=message;_toastUntil=_time+duration;}
    private static PanelContainer Panel(Control parent,Color color){var p=new PanelContainer {MouseFilter=Control.MouseFilterEnum.Stop};p.AddThemeStyleboxOverride("panel",StudioTheme.Box(color,7));parent.AddChild(p);return p;}
    private static MarginContainer Inset(Control parent,int horizontal,int vertical){var m=new MarginContainer();foreach(string side in new[]{"left","right"})m.AddThemeConstantOverride("margin_"+side,horizontal);foreach(string side in new[]{"top","bottom"})m.AddThemeConstantOverride("margin_"+side,vertical);parent.AddChild(m);return m;}
    private static VBoxContainer Column(Control parent,int spacing){var box=new VBoxContainer();box.AddThemeConstantOverride("separation",spacing);parent.AddChild(box);return box;}
    private static HBoxContainer Row(Control parent){var box=new HBoxContainer();box.AddThemeConstantOverride("separation",8);parent.AddChild(box);return box;}
    private static Label Label(string text,int size=14,Color? color=null){var l=new Label {Text=text,MouseFilter=Control.MouseFilterEnum.Ignore,VerticalAlignment=VerticalAlignment.Center};l.AddThemeFontSizeOverride("font_size",size);l.AddThemeColorOverride("font_color",color??StudioTheme.Text);return l;}
    private static Button Button(Control parent,string text,Action pressed){var b=new Button {Text=text,FocusMode=Control.FocusModeEnum.None,MouseDefaultCursorShape=Control.CursorShape.PointingHand};parent.AddChild(b);b.Pressed+=pressed;return b;}
}
