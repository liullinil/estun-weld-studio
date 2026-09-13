using Godot;
using System;
using System.Collections.Generic;
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
    public CadPart? Part {get;private set;}
    public WeldProgram? Program {get;private set;}
    public bool IsBusy {get;private set;}
    public bool IsSimulating {get;private set;}
    public WeldingEffects Effects {get;private set;}=null!;
    private RobotModel _robot=null!;private RobotController _controller=null!;private Camera3D _camera=null!;
    private Node3D _scene=null!;private Control _ui=null!;private TransformGizmo _gizmo=null!;
    private Panel _panel=null!;private VBoxContainer _list=null!;private Label _status=null!,_file=null!,_counts=null!,_progress=null!;
    private SpinBox _min=null!,_max=null!;private readonly SpinBox[] _pose=new SpinBox[6];
    private Button _run=null!,_plan=null!;private CheckBox _concave=null!;
    private CancellationTokenSource? _cancel;private int _revision,_motionIndex;
    private bool _exiting;
    private float _elapsed;private float[] _motionStart=Array.Empty<float>();private bool _syncPose;
    private CollisionScene? _collision;private RobotCapsule[]? _capsules;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages=new();
    private readonly Color _ink=new("e7efef"),_muted=new("96a9b3"),_accent=new("f4b65c");
    public bool EditingVisible=>_panel.Visible;
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
        _panel=new Panel {Name="WeldingWorkspace",Position=new Vector2(28,300),Size=new Vector2(320,628),MouseFilter=Control.MouseFilterEnum.Stop};
        _panel.AddThemeStyleboxOverride("panel",new StyleBoxFlat {BgColor=new Color("1c2730"),BorderColor=new Color("40515c"),BorderWidthBottom=1,BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,CornerRadiusBottomLeft=10,CornerRadiusBottomRight=10,CornerRadiusTopLeft=10,CornerRadiusTopRight=10});_ui.AddChild(_panel);
        LabelAt(_panel,"WELD PREPARATION",16,13,277,27,17,_ink);
        ButtonAt(_panel,"×",278,10,27,27,()=>{_panel.Hide();_gizmo.Active=false;});
        ButtonAt(_panel,"IMPORT STEP",16,53,139,35,ChooseStep);ButtonAt(_panel,"LOAD SAMPLE",166,53,138,35,LoadSample);
        _file=LabelAt(_panel,"No workpiece loaded",16,96,286,25,12,_muted);_file.TextOverrunBehavior=TextServer.OverrunBehavior.TrimEllipsis;
        LabelAt(_panel,"PART TRANSFORM",16,131,285,20,10,_accent);
        ButtonAt(_panel,"MOVE  /  W",16,157,139,30,()=>{if(IsBusy||IsSimulating)return;_gizmo.RotationMode=false;_gizmo.Active=true;});
        ButtonAt(_panel,"ROTATE  /  E",166,157,138,30,()=>{if(IsBusy||IsSimulating)return;_gizmo.RotationMode=true;_gizmo.Active=true;});
        for(int i=0;i<6;i++)
        {
            int axis=i;float x=16+(i%3)*98,y=i<3?209:247;
            LabelAt(_panel,i<3?new[]{"X mm","Y mm","Z mm"}[i]:new[]{"RX °","RY °","RZ °"}[i-3],x,y-15,95,16,9,_muted);
            var spin=new SpinBox {Position=new Vector2(x,y),Size=new Vector2(90,25),MinValue=i<3?-10000:-360,MaxValue=i<3?10000:360,Step=i<3?1:1,AllowGreater=false,AllowLesser=false};
            spin.AddThemeFontSizeOverride("font_size",11);_panel.AddChild(spin);_pose[i]=spin;spin.ValueChanged+=v=>ApplyPose(axis,(float)v);
        }
        LabelAt(_panel,"SEAM ANGLE RANGE",16,288,220,20,10,_accent);
        _min=Spin(16,316,99,85,0,180);_max=Spin(126,316,99,95,0,180);
        LabelAt(_panel,"to",118,318,10,23,10,_muted);LabelAt(_panel,"degrees",237,318,72,23,11,_muted);
        _min.ValueChanged+=_=>FilterChanged();_max.ValueChanged+=_=>FilterChanged();
        _concave=new CheckBox {Text="Concave / contact only",Position=new Vector2(11,353),Size=new Vector2(235,29),ButtonPressed=true};_concave.AddThemeFontSizeOverride("font_size",11);_panel.AddChild(_concave);_concave.Toggled+=_=>FilterChanged();
        ButtonAt(_panel,"RESTORE",234,352,71,27,()=>{Part?.Restore();Invalidate();RefreshList();});
        _counts=LabelAt(_panel,"Candidates appear after import",16,391,287,23,11,_muted);
        var scroll=new ScrollContainer {Position=new Vector2(14,420),Size=new Vector2(294,105),HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};_panel.AddChild(scroll);
        _list=new VBoxContainer {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};scroll.AddChild(_list);
        _plan=ButtonAt(_panel,"GENERATE PROGRAM",16,538,288,34,Generate);
        _run=ButtonAt(_panel,"SIMULATE",16,581,139,33,Run);ButtonAt(_panel,"EXPORT",166,581,138,33,Export);
        _status=LabelAt(_ui,"",361,781,722,27,12,_ink);_progress=LabelAt(_ui,"",361,811,722,24,11,_muted);
        _status.HorizontalAlignment=HorizontalAlignment.Center;_progress.HorizontalAlignment=HorizontalAlignment.Center;
    }
    private SpinBox Spin(float x,float y,float width,float value,float min,float max)
    {
        var box=new SpinBox {Position=new Vector2(x,y),Size=new Vector2(width,28),Value=value,MinValue=min,MaxValue=max,Step=1};box.AddThemeFontSizeOverride("font_size",12);_panel.AddChild(box);return box;
    }
    public void Toggle(){_panel.Visible=!_panel.Visible;_gizmo.Active=_panel.Visible && Part!=null&&!IsBusy&&!IsSimulating;}
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
            _gizmo.Target=Part;_gizmo.Active=true;_panel.Show();
            Part.SeamSelectionChanged+=RefreshList;_file.Text=doc.Name+"  ·  STEP";
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
            string id=seam.Id;var row=new HBoxContainer();_list.AddChild(row);
            var button=new Button {Text=$"{seam.Id}   {seam.Length*1000:0} mm   {seam.AngleDegrees:0}°",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,Alignment=HorizontalAlignment.Left,FocusMode=Control.FocusModeEnum.None};
            button.AddThemeFontSizeOverride("font_size",11);row.AddChild(button);button.Pressed+=()=>Part.Select(id);
            var result=Program?.Seams.FirstOrDefault(s=>s.Id==id);button.TooltipText=result?.Reason??seam.Kind;
            if(result!=null)button.Modulate=result.State==WeldSeamState.Ready?new Color("8bdfb6"):new Color("ed868a");
            if(id==Part.SelectedSeam)button.Modulate=new Color("ffce7f");
            var remove=new Button {Text="×",CustomMinimumSize=new Vector2(28,25),FocusMode=Control.FocusModeEnum.None,TooltipText="Exclude this candidate seam"};row.AddChild(remove);
            remove.Pressed+=()=>{seam.Deleted=true;Invalidate();RefreshList();};
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
            var linkVertices=new Dictionary<int,Vector3[]>();
            if(_capsules==null)
            {
                foreach(var mesh in Descendants(_robot).OfType<MeshInstance3D>())
                {
                    if(!mesh.Name.ToString().StartsWith("CAD_Link"))continue;int link=int.Parse(mesh.Name.ToString()[8].ToString());
                    Vector3[] points=mesh.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                    linkVertices[link]=linkVertices.TryGetValue(link,out var old)?old.Concat(points).ToArray():points;
                }
            }
            var rest=_controller.RestTransforms;var tool=_controller.ToolTransform;float[] start=_controller.AnglesDegrees;
            var plinth=new CylinderMesh {TopRadius=.70f,BottomRadius=.70f,Height=.201f,RadialSegments=64};
            Vector3[] fixtures=plinth.GetFaces().Select(p=>p+new Vector3(0,-.1095f,0)).ToArray();
            string name=Part.Document.Name;var token=_cancel.Token;
            Program=await Task.Run(()=>
            {
                if(_capsules==null)
                {
                    var caps=CollisionScene.CreateRobotCapsules(linkVertices).ToList();
                    caps.AddRange(WeldTorch.CollisionVolumes());_capsules=caps.ToArray();
                }
                _collision=new CollisionScene(triangles,_capsules,rest,staticTriangles:fixtures);
                return WeldPlanner.Plan(new WeldPlanRequest {Seams=candidates,PartTransform=partTransform,JointRest=rest,ToolTransform=tool,StartAngles=start,Collision=_collision,PartName=name},token,(p,s)=>_messages.Enqueue($"{p*100:0}%  ·  {s}"));
            },token);
            if(_exiting||revision!=_revision){Program=null;return;}
            foreach(var seam in Program.Seams)Part.SetResult(seam.Id,seam.State==WeldSeamState.Ready);
            Part.RefreshSeams();RefreshList();_status.Text=Program.Summary;
            _progress.Text="GREEN: checked path   ·   RED: unavailable   ·   SPACE + SIMULATE";Toast?.Invoke(Program.Summary);
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
        if(!_controller.CanMove){Toast?.Invoke("Enable DRIVES and hold SPACE, then click SIMULATE");return;}
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
            if(key.Keycode==Key.W && !IsBusy&&!IsSimulating){_gizmo.RotationMode=false;_gizmo.Active=true;}
            if(key.Keycode==Key.E && !IsBusy&&!IsSimulating){_gizmo.RotationMode=true;_gizmo.Active=true;}
            if(key.Keycode==Key.Delete && Part.DeleteSelected()){Invalidate();RefreshList();GetViewport().SetInputAsHandled();}
        }
        if(input is InputEventMouseButton {ButtonIndex:MouseButton.Left,Pressed:true} click && !IsBusy)
        {
            if(_gizmo.TryBeginDrag(click.Position)){GetViewport().SetInputAsHandled();return;}
            if(Part.PickSeam(click.Position))GetViewport().SetInputAsHandled();
        }
    }
    public override void _ExitTree(){_exiting=true;_cancel?.Cancel();}
    private static IEnumerable<Node> Descendants(Node node){foreach(Node child in node.GetChildren()){yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private Label LabelAt(Control p,string text,float x,float y,float w,float h,int size,Color color){var label=new Label {Text=text,Position=new(x,y),Size=new(w,h),MouseFilter=Control.MouseFilterEnum.Ignore};label.AddThemeFontSizeOverride("font_size",size);label.AddThemeColorOverride("font_color",color);p.AddChild(label);return label;}
    private Button ButtonAt(Control p,string text,float x,float y,float w,float h,Action action){var b=new Button {Text=text,Position=new(x,y),Size=new(w,h),FocusMode=Control.FocusModeEnum.None};b.AddThemeFontSizeOverride("font_size",11);b.AddThemeStyleboxOverride("normal",new StyleBoxFlat {BgColor=new Color("31424d"),CornerRadiusBottomLeft=5,CornerRadiusBottomRight=5,CornerRadiusTopLeft=5,CornerRadiusTopRight=5});b.AddThemeStyleboxOverride("hover",new StyleBoxFlat {BgColor=new Color("46606c")});p.AddChild(b);b.Pressed+=action;return b;}
}
