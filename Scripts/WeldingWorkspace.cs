using Godot;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
namespace EstunStudio;

/// <summary>STEP preparation, seam review, collision-aware path generation and offline playback.</summary>
public partial class WeldingWorkspace : Node
{
    public event Action<string>? Toast;
    public event Action<Vector3>? FocusRequested;
    public event Action? PanelVisibilityChanged;
    public CadPart? Part {get;private set;}
    public WeldProgram? Program {get;private set;}
    public bool IsBusy {get;private set;}
    public bool IsSimulating {get;private set;}
    public WeldingEffects Effects {get;private set;}=null!;
    private RobotModel _robot=null!;private RobotController _controller=null!;private Camera3D _camera=null!;
    private Node3D _scene=null!;private Control _ui=null!;private TransformGizmo _gizmo=null!;
    private Panel _panel=null!;private VBoxContainer _list=null!;private Label _status=null!,_file=null!,_counts=null!,_progress=null!;
    private SpinBox _min=null!,_max=null!;private readonly SpinBox[] _pose=new SpinBox[6];
    private Button _run=null!,_plan=null!,_move=null!,_rotate=null!;private CheckBox _concave=null!;
    private CancellationTokenSource? _cancel;private int _revision,_motionIndex;
    private bool _exiting;
    private float _elapsed;private float[] _motionStart=Array.Empty<float>();private bool _syncPose;
    private CollisionScene? _collision;private RobotCapsule[]? _capsules;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages=new();
    private readonly Color _ink=new("e7efef"),_muted=new("96a9b3");
    public bool EditingVisible=>_panel.Visible;
    public Control Panel=>_panel;
    public string StatusText=>_status.Text;
    public string ProgressText=>_progress.Text;
    public Task PlanningTask {get;private set;}=Task.CompletedTask;
    public void Build(Node3D scene,Control ui,RobotModel robot,RobotController controller,Camera3D camera)
    {
        _scene=scene;_ui=ui;_robot=robot;_controller=controller;_camera=camera;
        Effects=new WeldingEffects();scene.AddChild(Effects);Effects.Build();
        _gizmo=new TransformGizmo {Camera=camera};ui.AddChild(_gizmo);_gizmo.TransformChanged+=()=>{Invalidate();SyncPose();};
        BuildPanel();
    }
    private void BuildPanel()
    {
        _panel=new Panel {Name="WeldingWorkspace",Size=new Vector2(320,720),MouseFilter=Control.MouseFilterEnum.Stop};
        _panel.AddThemeStyleboxOverride("panel",new StyleBoxFlat {BgColor=new Color("1b2229"),BorderColor=new Color("343f48"),BorderWidthBottom=1,BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,CornerRadiusBottomLeft=8,CornerRadiusBottomRight=8,CornerRadiusTopLeft=8,CornerRadiusTopRight=8});
        _ui.AddChild(_panel);
        var margin=new MarginContainer {Name="PanelInsets"};_panel.AddChild(margin);margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach(string edge in new[]{"left","top","right","bottom"})margin.AddThemeConstantOverride("margin_"+edge,14);
        var layout=Column(margin,10);layout.Name="PreparationLayout";
        var header=Row(layout);Text(header,"Weld preparation",15,_ink).SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        var close=Button(header,"×",()=>SetPanelVisible(false),false);close.CustomMinimumSize=new Vector2(30,30);close.TooltipText="Hide weld preparation";
        Divider(layout);

        // Containers use real window pixels. On short displays only the form
        // scrolls, leaving the close and execution controls directly accessible.
        var formScroll=new ScrollContainer {Name="PreparationScroll",HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,SizeFlagsVertical=Control.SizeFlags.ExpandFill,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};layout.AddChild(formScroll);
        var form=Column(formScroll,9);form.SizeFlagsVertical=Control.SizeFlags.ExpandFill;
        var importRow=Row(form);Button(importRow,"Import STEP",ChooseStep);Button(importRow,"Load sample",LoadSample);
        _file=Text(form,"No workpiece loaded",13,_muted);_file.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;_file.TooltipText="Import a STEP workpiece to position it and find weld candidates.";
        Section(form,"Part placement");
        var modeRow=Row(form);
        _move=Button(modeRow,"Move  ·  W",()=>SetGizmoMode(false));_rotate=Button(modeRow,"Rotate  ·  E",()=>SetGizmoMode(true));
        _move.ToggleMode=true;_rotate.ToggleMode=true;_move.ButtonPressed=true;
        var poseGrid=new GridContainer {Name="PartCoordinates",Columns=3,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};poseGrid.AddThemeConstantOverride("h_separation",8);poseGrid.AddThemeConstantOverride("v_separation",6);form.AddChild(poseGrid);
        Text(poseGrid,"",12,_muted);Text(poseGrid,"Position · mm",12,_muted);Text(poseGrid,"Rotation · °",12,_muted);
        for(int i=0;i<3;i++)
        {
            Text(poseGrid,new[]{"X","Y","Z"}[i],13,_muted).CustomMinimumSize=new Vector2(20,30);
            foreach(int axis in new[]{i,i+3})
            {
                var spin=Spin(poseGrid,0,axis<3?-10000:-360,axis<3?10000:360);spin.Name="PartPose"+axis;spin.TooltipText=(axis<3?"Position ":"Rotation ")+new[]{"X","Y","Z"}[i]+(axis<3?" in millimeters":" in degrees");
                _pose[axis]=spin;spin.ValueChanged+=v=>ApplyPose(axis,(float)v);
            }
        }
        Section(form,"Seam detection");
        var angleRow=Row(form);_min=Spin(angleRow,85,0,180);_min.Suffix="°";_min.TooltipText="Minimum seam angle";
        Text(angleRow,"to",13,_muted);_max=Spin(angleRow,95,0,180);_max.Suffix="°";_max.TooltipText="Maximum seam angle";
        _min.ValueChanged+=_=>FilterChanged();_max.ValueChanged+=_=>FilterChanged();
        _concave=new CheckBox {Text="Concave / contact only",ButtonPressed=true,CustomMinimumSize=new Vector2(0,30)};_concave.AddThemeFontSizeOverride("font_size",13);form.AddChild(_concave);_concave.Toggled+=_=>FilterChanged();
        var candidatesHeader=Row(form);_counts=Text(candidatesHeader,"No candidate seams",12,_muted);_counts.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;_counts.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;
        var restore=Button(candidatesHeader,"Restore",()=>{Part?.Restore();Invalidate();RefreshList();},false);restore.CustomMinimumSize=new Vector2(70,30);restore.TooltipText="Restore all excluded seam candidates";
        var seamScroll=new ScrollContainer {Name="SeamCandidates",CustomMinimumSize=new Vector2(0,128),HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,SizeFlagsVertical=Control.SizeFlags.ExpandFill};form.AddChild(seamScroll);
        _list=Column(seamScroll,4);
        Divider(layout);
        _plan=Button(layout,"GENERATE PROGRAM",Generate);_plan.CustomMinimumSize=new Vector2(0,36);
        var playback=Row(layout);_run=Button(playback,"SIMULATE",Run);Button(playback,"EXPORT",Export);
        // Main displays these messages in its responsive status bar; keep the
        // existing update paths without placing free-floating viewport labels.
        _status=Text(_panel,"",13,_ink);_status.Name="WeldStatus";_status.Hide();
        _progress=Text(_panel,"",12,_muted);_progress.Name="WeldProgress";_progress.Hide();
    }
    private SpinBox Spin(Control parent,float value,float min,float max)
    {
        var box=new SpinBox {CustomMinimumSize=new Vector2(80,30),SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,Value=value,MinValue=min,MaxValue=max,Step=1,AllowGreater=false,AllowLesser=false};
        box.AddThemeFontSizeOverride("font_size",14);
        var input=box.GetLineEdit();input.AddThemeFontSizeOverride("font_size",14);input.AddThemeColorOverride("font_color",_ink);input.Alignment=HorizontalAlignment.Right;
        input.AddThemeStyleboxOverride("normal",ButtonStyle("202a32","394650"));input.AddThemeStyleboxOverride("focus",ButtonStyle("26323c","bd9657"));
        parent.AddChild(box);return box;
    }
    public void Toggle()=>SetPanelVisible(!_panel.Visible);
    public void SetPanelVisible(bool visible)
    {
        bool changed=_panel.Visible!=visible;_panel.Visible=visible;_gizmo.Active=visible&&Part!=null&&!IsBusy&&!IsSimulating;
        if(changed)PanelVisibilityChanged?.Invoke();
    }
    private void SetGizmoMode(bool rotate)
    {
        if(IsBusy||IsSimulating)return;
        _gizmo.RotationMode=rotate;_gizmo.Active=_panel.Visible&&Part!=null;_move.SetPressedNoSignal(!rotate);_rotate.SetPressedNoSignal(rotate);
    }
    private void ChooseStep()
    {
        if(IsBusy)return;
        var dialog=new FileDialog {Title="Import STEP workpiece",Access=FileDialog.AccessEnum.Filesystem,FileMode=FileDialog.FileModeEnum.OpenFile,UseNativeDialog=true,Filters=new[]{"*.step,*.stp ; STEP CAD"}};
        AddChild(dialog);dialog.FileSelected+=async path=>{dialog.QueueFree();await Import(path);};dialog.Canceled+=dialog.QueueFree;dialog.PopupCenteredRatio(.75f);
    }
    public async void LoadSample()
    {
        string source="res://Assets/Samples/WeldingBracket.step";
        if(!Godot.FileAccess.FileExists(source)){Toast?.Invoke("Sample STEP is not installed");return;}
        string path=ProjectSettings.GlobalizePath("user://WeldingBracket.step");
        using(var output=Godot.FileAccess.Open(path,Godot.FileAccess.ModeFlags.Write))output.StoreBuffer(Godot.FileAccess.GetFileAsBytes(source));
        await Import(path);
    }
    public async Task Import(string path)
    {
        if(IsBusy)return;Stop();IsBusy=true;_plan.Disabled=true;_cancel=new CancellationTokenSource();
        try
        {
            var doc=await new CadImportService().ImportAsync(path,new Progress<string>(s=>_messages.Enqueue(s)),_cancel.Token);
            if(_exiting)return;
            Part?.QueueFree();Part=new CadPart {Name="STEP assembly"};_scene.AddChild(Part);Part.Build(doc,_camera);
            Part.Position=new Vector3(.85f,.27f,0)-doc.Bounds.GetCenter();Part.Position+=Vector3.Up*(doc.Bounds.Size.Y*.5f);
            _gizmo.Target=Part;SetPanelVisible(true);_gizmo.Active=true;
            Part.SeamSelectionChanged+=RefreshList;_file.Text=doc.Name+"  ·  STEP";_file.TooltipText=$"{doc.Name} · {doc.SolidCount} solids · {doc.TriangleCount:N0} triangles";
            Invalidate();SyncPose();FilterChanged();
            Toast?.Invoke($"STEP imported · {doc.SolidCount} solids · {doc.TriangleCount:N0} triangles");
            _messages.Enqueue($"{doc.Seams.Count} angular edge candidates · review before generating");
            if(doc.Warnings.Length>0){_status.Text="Import notes · "+string.Join(" · ",doc.Warnings);Toast?.Invoke(doc.Warnings[0]);}
            FocusRequested?.Invoke(Part.GlobalPosition+doc.Bounds.GetCenter());
        }
        catch(OperationCanceledException){if(!_exiting)Toast?.Invoke("STEP import cancelled");}
        catch(Exception error){if(!_exiting){Toast?.Invoke(error.Message);_status.Text="Import failed · "+error.Message;}}
        finally{IsBusy=false;if(!_exiting)_plan.Disabled=false;_cancel?.Dispose();_cancel=null;}
    }
    private void ApplyPose(int axis,float value)
    {
        if(_syncPose||Part==null||IsBusy)return;
        if(axis<3){Vector3 p=Part.Position;p[axis]=value*.001f;Part.Position=p;}
        else{Vector3 r=Part.RotationDegrees;r[axis-3]=value;Part.RotationDegrees=r;}
        Invalidate();
    }
    private void SyncPose()
    {
        if(Part==null)return;_syncPose=true;
        for(int i=0;i<3;i++){_pose[i].Value=Part.Position[i]*1000;_pose[i+3].Value=Part.RotationDegrees[i];}_syncPose=false;
    }
    private void FilterChanged()
    {
        if(Part==null)return;
        Part.MinimumAngle=(float)_min.Value;Part.MaximumAngle=(float)_max.Value;Part.ConcaveOnly=_concave.ButtonPressed;
        Invalidate();Part.RefreshSeams();RefreshList();
    }
    private void Invalidate()
    {
        _revision++;_cancel?.Cancel();Stop();Program=null;_collision=null;Effects.Clear();Part?.ResetResults();
        _status.Text="Workpiece changed · generate a new program";
    }
    private void RefreshList()
    {
        foreach(Node row in _list.GetChildren())row.QueueFree();
        if(Part==null)return;var seams=Part.Candidates();_counts.Text=$"{seams.Count} seams  /  {Part.Document.Seams.Count(s=>s.Deleted)} excluded";
        foreach(var seam in seams.Take(100))
        {
            string id=seam.Id;var row=Row(_list);
            var button=Button(row,$"{seam.Id}  ·  {seam.Length*1000:0} mm  ·  {seam.AngleDegrees:0}°",()=>Part.Select(id));button.Alignment=HorizontalAlignment.Left;button.ClipText=true;
            var result=Program?.Seams.FirstOrDefault(s=>s.Id==id);button.TooltipText=result?.Reason??seam.Kind;
            if(result!=null)button.Modulate=result.State==WeldSeamState.Ready?new Color("8bdfb6"):new Color("ed868a");
            if(id==Part.SelectedSeam)button.Modulate=new Color("ffce7f");
            var remove=Button(row,"×",()=>{seam.Deleted=true;Invalidate();RefreshList();},false);remove.CustomMinimumSize=new Vector2(30,32);remove.TooltipText="Exclude this candidate seam";
        }
    }
    public void Generate(){if(IsBusy){_cancel?.Cancel();return;}PlanningTask=GenerateAsync();}
    public async Task GenerateAsync()
    {
        if(IsBusy){_cancel?.Cancel();return;}
        if(Part==null){Toast?.Invoke("Import a STEP workpiece first");return;}
        var candidates=Part.Candidates();if(candidates.Count==0){Toast?.Invoke("No seams in the selected angle range");return;}
        Stop();Program=null;_collision=null;Part.ResetResults();RefreshList();IsBusy=true;_gizmo.Active=false;_cancel=new CancellationTokenSource();int revision=_revision;_plan.Text="CANCEL PLANNING";
        try
        {
            Transform3D partTransform=_robot.GlobalTransform.AffineInverse()*Part.GlobalTransform;
            Vector3[] triangles=Part.Document.Indices.Select(i=>partTransform*Part.Document.Vertices[i]).ToArray();
            // Collision fitting needs the unchanged source triangles, while the
            // renderer can share vertices through its lossless index buffers.
            var linkVertices=_capsules==null?_robot.GetLinkTriangles():null;
            var rest=_controller.RestTransforms;var tool=_controller.ToolTransform;float[] start=_controller.AnglesDegrees;
            var plinth=new CylinderMesh {TopRadius=.70f,BottomRadius=.70f,Height=.201f,RadialSegments=64};
            Vector3[] fixtures=plinth.GetFaces().Select(p=>p+new Vector3(0,-.1095f,0)).ToArray();
            string name=Part.Document.Name;var token=_cancel.Token;
            Program=await Task.Run(()=>
            {
                if(_capsules==null)
                {
                    var caps=CollisionScene.CreateRobotCapsules(linkVertices!).ToList();
                    caps.AddRange(WeldTorch.CollisionVolumes());_capsules=caps.ToArray();
                }
                _collision=new CollisionScene(triangles,_capsules,rest,staticTriangles:fixtures);
                return WeldPlanner.Plan(new WeldPlanRequest {Seams=candidates,PartTransform=partTransform,JointRest=rest,ToolTransform=tool,StartAngles=start,Collision=_collision,PartName=name},token,(p,s)=>_messages.Enqueue($"{p*100:0}%  ·  {s}"));
            },token);
            if(_exiting||revision!=_revision){Program=null;return;}
            foreach(var seam in Program.Seams)Part.SetResult(seam.Id,seam.State==WeldSeamState.Ready);
            Part.RefreshSeams();RefreshList();_status.Text=Program.Summary;
            _progress.Text="GREEN: checked path   ·   RED: unavailable   ·   CLICK SIMULATE";Toast?.Invoke(Program.Summary);
        }
        catch(OperationCanceledException){Program=null;if(!_exiting)_status.Text="Planning cancelled";}
        catch(Exception ex){Program=null;if(!_exiting){_status.Text="Planning failed · "+ex.Message;Toast?.Invoke(ex.Message);}}
        finally{IsBusy=false;if(!_exiting){_plan.Text="GENERATE PROGRAM";_gizmo.Active=_panel.Visible;}_cancel?.Dispose();_cancel=null;}
    }
    public void Run()
    {
        if(IsBusy){Toast?.Invoke("Wait for planning to finish");return;}
        if(IsSimulating){Stop();return;}
        if(Program==null||Program.Motions.Count==0){Toast?.Invoke("Generate a program with reachable seams first");return;}
        if(!_controller.CanMove){Toast?.Invoke("Enable DRIVES, then click SIMULATE");return;}
        if(_controller.AnglesDegrees.Zip(Program.StartAngles,(a,b)=>Math.Abs(a-b)).Max()>.3f){Toast?.Invoke("Robot start pose changed · generate the program again");return;}
        Effects.Clear();_motionIndex=0;_elapsed=0;_motionStart=_controller.AnglesDegrees;
        if(!_controller.ApplyPlannedPose(_motionStart,"Welding program ready"))return;
        IsSimulating=true;_gizmo.Active=false;_run.Text="STOP";
    }
    public void Stop()
    {
        if(IsSimulating)_controller.StopMotion();IsSimulating=false;Effects?.SetArc(false,Vector3.Zero,Vector3.Up);if(_run!=null)_run.Text="SIMULATE";
    }
    public override void _PhysicsProcess(double delta)
    {
        if(!IsSimulating||Program==null)return;
        if(!_controller.CanMove || !_controller.IsPlannedMotion || _motionIndex>=Program.Motions.Count){Stop();return;}
        WeldMotion motion=Program.Motions[_motionIndex];_elapsed+=(float)Math.Min(delta,.05)*_controller.SpeedOverride;
        float t=Mathf.Clamp(_elapsed/Mathf.Max(motion.DurationSeconds,.016f),0,1);float[] angles=new float[6];
        for(int i=0;i<6;i++)angles[i]=Mathf.Lerp(_motionStart[i],motion.TargetAngles[i],t);
        if(_collision!=null&&!_collision.Check(angles,out string reason)){Stop();_status.Text="Simulation stopped · "+reason;return;}
        if(!_controller.ApplyPlannedPose(angles,motion.ArcOn?"Welding · "+motion.SeamId:"Collision-checked transfer")){Stop();return;}
        Vector3 actual=_controller.TcpPosition;Vector3 approach=_controller.TcpTransform.Basis.Y;
        Effects.SetArc(motion.ArcOn,_robot.GlobalTransform*(actual-approach*.003f),_robot.GlobalBasis*approach);
        _status.Text=$"{motion.Kind}  {motion.SeamId}  ·  {_motionIndex+1}/{Program.Motions.Count}";
        if(t>=1){_motionIndex++;_elapsed=0;_motionStart=angles;if(_motionIndex>=Program.Motions.Count){Stop();_status.Text="Welding simulation complete";}}
    }
    private void Export()
    {
        if(Program==null||Program.Motions.Count==0){Toast?.Invoke("Generate a program before exporting");return;}
        WeldProgram snapshot=Program;
        var dialog=new FileDialog {Title="Export offline welding program",Access=FileDialog.AccessEnum.Filesystem,FileMode=FileDialog.FileModeEnum.SaveFile,UseNativeDialog=true,CurrentFile="weld-program.json",Filters=new[]{"*.json ; Offline robot program","*.csv ; Robot motion table"}};AddChild(dialog);
        dialog.FileSelected+=path=>{try{System.IO.File.WriteAllText(path,path.EndsWith(".csv",StringComparison.OrdinalIgnoreCase)?snapshot.ToCsv():snapshot.ToJson());Toast?.Invoke("Offline program exported · "+Path.GetFileName(path));}catch(Exception e){Toast?.Invoke(e.Message);}dialog.QueueFree();};dialog.Canceled+=dialog.QueueFree;dialog.PopupCenteredRatio(.7f);
    }
    public override void _Process(double delta)
    {
        while(_messages.TryDequeue(out string? s))_progress.Text=s;
        if(IsSimulating||IsBusy)_gizmo.Active=false;
    }
    public override void _UnhandledInput(InputEvent input)
    {
        if(Part==null)return;
        if(input is InputEventKey {Pressed:true,Echo:false} key)
        {
            if(key.Keycode==Key.W && !IsBusy&&!IsSimulating)SetGizmoMode(false);
            if(key.Keycode==Key.E && !IsBusy&&!IsSimulating)SetGizmoMode(true);
            if(key.Keycode==Key.Delete && Part.DeleteSelected()){Invalidate();RefreshList();GetViewport().SetInputAsHandled();}
        }
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:true} click && !IsBusy)
        {
            if(_gizmo.TryBeginDrag(click.Position)){GetViewport().SetInputAsHandled();return;}
            if(Part.PickSeam(click.Position))GetViewport().SetInputAsHandled();
        }
    }
    public override void _ExitTree(){_exiting=true;_cancel?.Cancel();}
    private static VBoxContainer Column(Control parent,int spacing)
    {
        var column=new VBoxContainer {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};column.AddThemeConstantOverride("separation",spacing);parent.AddChild(column);return column;
    }
    private static HBoxContainer Row(Control parent)
    {
        var row=new HBoxContainer {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};row.AddThemeConstantOverride("separation",8);parent.AddChild(row);return row;
    }
    private Label Text(Control parent,string text,int size,Color color)
    {
        var label=new Label {Text=text,MouseFilter=Control.MouseFilterEnum.Ignore};label.AddThemeFontSizeOverride("font_size",size);label.AddThemeColorOverride("font_color",color);parent.AddChild(label);return label;
    }
    private void Section(Control parent,string title)
    {
        Divider(parent);Text(parent,title,14,_ink);
    }
    private static void Divider(Control parent)
    {
        var line=new HSeparator();line.AddThemeStyleboxOverride("separator",new StyleBoxLine {Color=new Color("343f48"),Thickness=1});parent.AddChild(line);
    }
    private Button Button(Control parent,string text,Action action,bool expand=true)
    {
        var button=new Button {Text=text,CustomMinimumSize=new Vector2(0,32),SizeFlagsHorizontal=expand?Control.SizeFlags.ExpandFill:Control.SizeFlags.Fill,FocusMode=Control.FocusModeEnum.None};
        button.AddThemeFontSizeOverride("font_size",14);button.AddThemeColorOverride("font_color",_ink);button.AddThemeColorOverride("font_hover_color",Colors.White);button.AddThemeColorOverride("font_pressed_color",Colors.White);
        button.AddThemeStyleboxOverride("normal",ButtonStyle("29343d","394650"));button.AddThemeStyleboxOverride("hover",ButtonStyle("364550","526472"));button.AddThemeStyleboxOverride("pressed",ButtonStyle("3b454a","bd9657"));button.AddThemeStyleboxOverride("disabled",ButtonStyle("252d34","313a42"));
        parent.AddChild(button);button.Pressed+=action;return button;
    }
    private static StyleBoxFlat ButtonStyle(string background,string border)=>new()
    {
        BgColor=new Color(background),BorderColor=new Color(border),BorderWidthLeft=1,BorderWidthTop=1,BorderWidthRight=1,BorderWidthBottom=1,
        CornerRadiusBottomLeft=4,CornerRadiusBottomRight=4,CornerRadiusTopLeft=4,CornerRadiusTopRight=4,ContentMarginLeft=8,ContentMarginRight=8,ContentMarginTop=4,ContentMarginBottom=4
    };
}
