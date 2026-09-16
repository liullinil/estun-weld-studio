using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
namespace EstunStudio;

/// <summary>Native CAD, explicit seam assignment, warning playback and Lua workflow.</summary>
public partial class WeldingWorkspace : Node
{
    public event Action<string>? Toast;
    public event Action<Vector3>? FocusRequested;
    public event Action? PanelVisibilityChanged;
    public event Action? WorkpieceChanged;
    public CadPart? Part { get; private set; }
    public WeldProgram? Program { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsSimulating { get; private set; }
    public bool IsPaused { get; private set; }
    public int[] CollisionLinks { get; private set; } = Array.Empty<int>();
    public string[] CollisionPairs { get; private set; } = Array.Empty<string>();
    public CollisionScene? Collision => _collision;
    public WeldingEffects Effects { get; private set; } = null!;
    public WeavingOptions Weaving { get; } = new();
    public bool EditingVisible => _panel.Visible;
    public Control Panel => _panel;
    public string StatusText => _status.Text;
    public string ProgressText => _progress.Text;
    public Task PlanningTask { get; private set; } = Task.CompletedTask;
    private bool _showGizmo = true;
    public bool ShowWorkpieceGizmo { get => _showGizmo; set { _showGizmo = value; UpdateGizmo(); } }
    private RobotModel _robot = null!; private RobotController _controller = null!; private Camera3D _camera = null!;
    private Node3D _scene = null!; private Control _ui = null!; private TransformGizmo _gizmo = null!;
    private Panel _panel = null!; private VBoxContainer _list = null!, _placement = null!, _detection = null!;
    private Label _status = null!, _file = null!, _counts = null!, _progress = null!, _propagationText = null!;
    private SpinBox _min = null!, _max = null!, _minLength = null!, _maxLength = null!;
    private HSlider _lengthLow = null!, _lengthHigh = null!, _propagationScope = null!;
    private readonly SpinBox[] _pose = new SpinBox[6];
    private Button _run = null!, _pause = null!, _stop = null!, _plan = null!, _cancelButton = null!, _export = null!, _move = null!, _rotate = null!;
    private CheckBox _concave = null!, _weave = null!; private AngleRangeControl _angles = null!;
    private ProgressBar _progressBar = null!; private PanelContainer _propagation = null!, _hint = null!; private Label _hintText = null!;
    private readonly List<BaseButton> _editingButtons = new();
    private CancellationTokenSource? _cancel; private int _revision, _motionIndex;
    private bool _exiting, _syncPose, _syncFilters, _syncPropagation;
    private float _elapsed; private float[] _motionStart = Array.Empty<float>(); private Stopwatch _watch = new();
    private CollisionScene? _collision; private RobotCapsule[]? _capsules; private IReadOnlyDictionary<int, Vector3[]>? _robotTriangles;
    private WeldPlanRequest? _request; private RobotPostprocessorResult? _postprocessed;
    private WeldMotion[] _playback = Array.Empty<WeldMotion>(); private WebBridge.CollisionFrame[][] _collisionFrames = Array.Empty<WebBridge.CollisionFrame[]>();
    private Node3D? _path; private string _operation = ""; private float _percent;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(float Percent, string Text)> _messages = new();
    private readonly Color _ink = new("e7efef"), _muted = new("96a9b3");

    public void Build(Node3D scene, Control ui, RobotModel robot, RobotController controller, Camera3D camera)
    {
        _scene = scene; _ui = ui; _robot = robot; _controller = controller; _camera = camera;
        Effects = new WeldingEffects(); scene.AddChild(Effects); Effects.Build();
        _gizmo = new TransformGizmo { Camera = camera }; ui.AddChild(_gizmo);
        _gizmo.TransformChanged += () => { Invalidate(true, true); SyncPose(); };
        BuildPanel(); BuildPropagation();
        _hint = new PanelContainer { Name = "WarningHint", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false }; ui.AddChild(_hint);
        _hint.AddThemeStyleboxOverride("panel", ButtonStyle("39242d", "ff405a")); _hintText = Text(_hint, "", 12, new Color("ffd3db"));
        UpdateControls();
    }
    private void BuildPanel()
    {
        _panel = new Panel { Name = "WeldingWorkspace", Size = new Vector2(320, 720), MouseFilter = Control.MouseFilterEnum.Stop };
        _panel.AddThemeStyleboxOverride("panel", ButtonStyle("1b2229", "343f48")); _ui.AddChild(_panel);
        var margin = new MarginContainer { Name = "PanelInsets" }; _panel.AddChild(margin); margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (string edge in new[] { "left", "top", "right", "bottom" }) margin.AddThemeConstantOverride("margin_" + edge, 10);
        var layout = Column(margin, 6); layout.Name = "PreparationLayout";
        var header = Row(layout); Text(header, "Work assignment", 15, _ink).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Button(header, "×", () => SetPanelVisible(false), false).TooltipText = "Hide work assignment";
        var imports = Row(layout); EditButton(imports, "Import CAD", ChooseCad); EditButton(imports, "Load sample", LoadSample);
        _file = Text(layout, "STEP / IGES workpiece", 12, _muted); _file.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var tabs = Row(layout);
        EditButton(tabs, "Part placement", () => { _placement.Visible = !_placement.Visible; _detection.Hide(); });
        EditButton(tabs, "Seam detection", () => { _detection.Visible = !_detection.Visible; _placement.Hide(); });
        _placement = Column(layout, 5); _placement.Visible = false;
        var modes = Row(_placement); _move = EditButton(modes, "Move · W", () => SetGizmoMode(false)); _rotate = EditButton(modes, "Rotate · E", () => SetGizmoMode(true));
        _move.ToggleMode = _rotate.ToggleMode = true; _move.ButtonPressed = true;
        var grid = new GridContainer { Name = "PartCoordinates", Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; _placement.AddChild(grid);
        Text(grid, "Axis", 11, _muted); Text(grid, "Position · mm", 11, _muted); Text(grid, "Rotation · °", 11, _muted);
        for (int i = 0; i < 3; i++)
        {
            Text(grid, new[] { "X", "Y", "Z" }[i], 12, _muted);
            foreach (int axis in new[] { i, i + 3 }) { var spin = Spin(grid, 0, axis < 3 ? -10000 : -360, axis < 3 ? 10000 : 360); spin.Name = "PartPose" + axis; _pose[axis] = spin; spin.ValueChanged += v => ApplyPose(axis, (float)v); }
        }
        _detection = Column(layout, 4); _detection.Visible = false;
        _angles = new AngleRangeControl { Name = "SeamAngleProtractor", CustomMinimumSize = new Vector2(0, 130) }; _detection.AddChild(_angles);
        var angleRow = Row(_detection); Text(angleRow, "Angle", 12, _muted); _min = Spin(angleRow, 85, 0, 180, .1f); _min.Suffix = "°"; _max = Spin(angleRow, 95, 0, 180, .1f); _max.Suffix = "°";
        _angles.RangeChanged += (min, max) => { _syncFilters = true; _min.Value = min; _max.Value = max; _syncFilters = false; FilterChanged(); };
        _min.ValueChanged += _ => { if (_syncFilters) return; _syncFilters = true; if (_max.Value < _min.Value) _max.Value = _min.Value; _syncFilters = false; FilterChanged(); };
        _max.ValueChanged += _ => { if (_syncFilters) return; _syncFilters = true; if (_min.Value > _max.Value) _min.Value = _max.Value; _syncFilters = false; FilterChanged(); };
        var lengthRow = Row(_detection); Text(lengthRow, "Length", 12, _muted); _minLength = Spin(lengthRow, 0, 0, 10000, .1f); _maxLength = Spin(lengthRow, 1000, 0, 10000, .1f); _minLength.Suffix = _maxLength.Suffix = "mm";
        _lengthLow = Slider(_detection, 0, 1000, 0, .1); _lengthHigh = Slider(_detection, 0, 1000, 1000, .1);
        _lengthLow.TooltipText = "Minimum seam length"; _lengthHigh.TooltipText = "Maximum seam length";
        _lengthLow.ValueChanged += v => { if (!_syncFilters) _minLength.Value = v; }; _lengthHigh.ValueChanged += v => { if (!_syncFilters) _maxLength.Value = v; };
        _minLength.ValueChanged += _ => LengthChanged(true); _maxLength.ValueChanged += _ => LengthChanged(false);
        _concave = new CheckBox { Text = "Concave / contact only", ButtonPressed = false }; _concave.AddThemeFontSizeOverride("font_size", 12); _detection.AddChild(_concave); _concave.Toggled += _ => FilterChanged();
        var weaveRow = Row(layout); _weave = new CheckBox { Text = "Weaving", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; weaveRow.AddChild(_weave);
        _weave.Toggled += value => { Weaving.Enabled = value; Invalidate(true); }; EditButton(weaveRow, "Settings", ShowWeaving, false);
        _counts = Text(layout, "No potential seams", 12, _muted); _counts.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var select = Row(layout); EditButton(select, "SelectAll", SelectAll); EditButton(select, "unSelectAll", UnselectAll); EditButton(select, "Disable warnings", DisableWarnings);
        var scroll = new ScrollContainer { Name = "SeamCandidates", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill }; layout.AddChild(scroll);
        _list = Column(scroll, 4);
        _progress = Text(layout, "Select seams to process", 11, _muted); _progress.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _progressBar = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 5) }; layout.AddChild(_progressBar);
        var generate = Row(layout); _plan = Button(generate, "Generate program", Generate); _cancelButton = Button(generate, "Cancel", () => _cancel?.Cancel(), false); _cancelButton.Visible = false;
        var playback = Row(layout); _run = Button(playback, "Simulate", Run); _pause = Button(playback, "Pause", Pause); _stop = Button(playback, "Stop", Stop); _export = Button(playback, "Export", Export);
        _status = Text(_panel, "Select potential seams for the work assignment", 12, _ink); _status.Name = "WeldStatus"; _status.Hide();
    }
    private void BuildPropagation()
    {
        _propagation = new PanelContainer { Name = "Propagation", Visible = false, Size = new Vector2(280, 116), ZIndex = 30, MouseFilter = Control.MouseFilterEnum.Stop }; _ui.AddChild(_propagation);
        _propagation.AddThemeStyleboxOverride("panel", ButtonStyle("26343d", "587783")); var column = Column(_propagation, 7);
        var row = Row(column); _propagationText = Text(row, "Propagation", 13, _ink); _propagationText.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; Button(row, "×", ClosePropagation, false);
        _propagationScope = Slider(column, 0, 1, 0, 1); _propagationScope.ValueChanged += value => { if (_syncPropagation || Part == null) return; Part.ApplyPropagation((int)value); UpdatePropagation(); };
        Text(column, "One     →     Neighbors     →     Entire contour", 11, _muted);
    }
    private void BeginSelection(string id, Vector2 position)
    {
        if (IsBusy || IsSimulating || Part == null) return; ClosePropagation(); Part.Select(id); var transaction = Part.Selection.Transaction; if (transaction == null) return;
        _syncPropagation = true; _propagationScope.MaxValue = Math.Max(1, transaction.Order.Length - 1); _propagationScope.Editable = transaction.Order.Length > 1; _propagationScope.Value = transaction.Scope; _syncPropagation = false;
        UpdatePropagation(); _propagation.Show(); _propagation.Position = new Vector2(Mathf.Clamp(position.X + 12, 4, Math.Max(4, _ui.Size.X - 292)), Mathf.Clamp(position.Y + 8, 4, Math.Max(4, _ui.Size.Y - 132)));
    }
    private void UpdatePropagation() { var t = Part?.Selection.Transaction; if (t != null) _propagationText.Text = $"{(t.Adding ? "Add" : "Remove")} · {t.Affected.Length} / {t.Order.Length} lines"; }
    private void ClosePropagation() { _propagation?.Hide(); Part?.Selection.Finish(); }
    public void SelectAll() { if (IsBusy || IsSimulating) return; ClosePropagation(); Part?.SelectAll(); }
    public void UnselectAll() { if (IsBusy || IsSimulating) return; ClosePropagation(); Part?.UnselectAll(); }
    public void DisableWarnings() { if (IsBusy || IsSimulating) return; ClosePropagation(); Part?.DisableWarnings(); }
    private void LengthChanged(bool minimum)
    {
        if (_syncFilters) return; _syncFilters = true;
        if (minimum && _minLength.Value > _maxLength.Value) _maxLength.Value = _minLength.Value;
        if (!minimum && _maxLength.Value < _minLength.Value) _minLength.Value = _maxLength.Value;
        _lengthLow.Value = _minLength.Value; _lengthHigh.Value = _maxLength.Value; _syncFilters = false; FilterChanged();
    }
    private void FilterChanged()
    {
        if (_syncFilters || IsBusy || IsSimulating) return; _angles.SetRange((float)_min.Value, (float)_max.Value);
        if (Part == null) return; ClosePropagation(); Part.MinimumAngle = (float)_min.Value; Part.MaximumAngle = (float)_max.Value; Part.MinimumLength = (float)_minLength.Value * .001f; Part.MaximumLength = (float)_maxLength.Value * .001f; Part.ConcaveOnly = _concave.ButtonPressed;
        Part.UpdateCandidates(); Invalidate(true); RefreshList();
    }
    private void UpdateGizmo() { if (_gizmo != null) _gizmo.Active = _showGizmo && _panel != null && _panel.Visible && Part != null && !IsBusy && !IsSimulating; }
    public void Toggle() => SetPanelVisible(!_panel.Visible);
    public void SetPanelVisible(bool visible) { bool changed = _panel.Visible != visible; _panel.Visible = visible; UpdateGizmo(); if (!visible) ClosePropagation(); if (changed) PanelVisibilityChanged?.Invoke(); }
    private void SetGizmoMode(bool rotate) { if (IsBusy || IsSimulating) return; _gizmo.RotationMode = rotate; UpdateGizmo(); _move.SetPressedNoSignal(!rotate); _rotate.SetPressedNoSignal(rotate); }
    private void ChooseCad()
    {
        if (IsBusy || IsSimulating) return;
        var dialog = new FileDialog { Title = "Import CAD workpiece", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.OpenFile, UseNativeDialog = true, Filters = new[] { "*.step,*.stp,*.iges,*.igs ; STEP / IGES CAD" } };
        AddChild(dialog); dialog.FileSelected += async path => { dialog.QueueFree(); await Import(path); }; dialog.Canceled += dialog.QueueFree; dialog.PopupCenteredRatio(.75f);
    }
    public async void LoadSample()
    {
        const string source = "res://Assets/Samples/WeldingBracket.step"; if (!Godot.FileAccess.FileExists(source)) { Toast?.Invoke("Sample CAD is not installed"); return; }
        try
        {
            System.IO.Directory.CreateDirectory(OS.GetUserDataDir());
            string path = ProjectSettings.GlobalizePath("user://WeldingBracket.step");
            if (!System.IO.File.Exists(path)) System.IO.File.WriteAllBytes(path, Godot.FileAccess.GetFileAsBytes(source));
            await Import(path);
        }
        catch (Exception error) { if (!_exiting) Toast?.Invoke("Sample import failed · " + error.Message); }
    }
    public async Task Import(string path)
    {
        if (IsBusy) return; Invalidate(true, true); BeginBusy("Importing CAD");
        try
        {
            var document = await new CadImportService().ImportAsync(path, new Progress<string>(s => _messages.Enqueue((-1, s))), _cancel!.Token); if (_exiting) return;
            Part?.QueueFree(); Part = new CadPart { Name = "CAD assembly" }; _scene.AddChild(Part); Part.Build(document, _camera);
            Part.Position = new Vector3(.85f, .27f, 0) - document.Bounds.GetCenter() + Vector3.Up * (document.Bounds.Size.Y * .5f); _gizmo.Target = Part;
            Part.SeamSelectionChanged += () => { Invalidate(); RefreshList(); };
            _file.Text = document.Name; _file.TooltipText = $"{document.SolidCount} solids · {document.TriangleCount:N0} triangles";
            float maximum = Math.Max(1, Mathf.Ceil(document.Seams.Select(s => s.Length * 1000).DefaultIfEmpty(1).Max()));
            _syncFilters = true; _minLength.Value = 0; _maxLength.MaxValue = _minLength.MaxValue = maximum; _maxLength.Value = maximum; _lengthLow.MaxValue = _lengthHigh.MaxValue = maximum; _lengthLow.Value = 0; _lengthHigh.Value = maximum; _syncFilters = false;
            Part.MinimumAngle = (float)_min.Value; Part.MaximumAngle = (float)_max.Value; Part.ConcaveOnly = _concave.ButtonPressed; Part.MaximumLength = maximum * .001f; Part.UpdateCandidates();
            SyncPose(); SetPanelVisible(true); RefreshList(); WorkpieceChanged?.Invoke();
            _operation = "CAD ready · select seams to process"; _percent = 100;
            _status.Text = document.Warnings.Length > 0 ? string.Join(" · ", document.Warnings) : "Choose potential seams for the work assignment";
            Toast?.Invoke($"CAD imported · {document.SolidCount} solids · {document.TriangleCount:N0} triangles"); FocusRequested?.Invoke(Part.GlobalPosition + document.Bounds.GetCenter());
        }
        catch (OperationCanceledException) { if (!_exiting) _status.Text = "CAD import cancelled"; }
        catch (Exception error) { if (!_exiting) { _status.Text = "Import failed · " + error.Message; Toast?.Invoke(error.Message); } }
        finally { EndBusy(); }
    }
    private void ApplyPose(int axis, float value)
    {
        if (_syncPose || Part == null || IsBusy || IsSimulating) return;
        if (axis < 3) { var position = Part.Position; position[axis] = value * .001f; Part.Position = position; } else { var rotation = Part.RotationDegrees; rotation[axis - 3] = value; Part.RotationDegrees = rotation; }
        Invalidate(true, true);
    }
    private void SyncPose() { if (Part == null) return; _syncPose = true; for (int i = 0; i < 3; i++) { _pose[i].Value = Part.Position[i] * 1000; _pose[i + 3].Value = Part.RotationDegrees[i]; } _syncPose = false; }
    private void Invalidate(bool clearResults = false, bool geometryChanged = false)
    {
        _revision++; _cancel?.Cancel(); FinishSimulation(); Program = null; _request = null; _postprocessed = null; _playback = Array.Empty<WeldMotion>(); ClearPath(); Effects.Clear();
        if (clearResults) Part?.ResetResults(); if (geometryChanged) { _collision = null; WorkpieceChanged?.Invoke(); }
        if (_status != null) _status.Text = "Assignment changed · generate a new program"; UpdateControls();
    }
    private void RefreshList()
    {
        foreach (Node node in _list.GetChildren()) { _list.RemoveChild(node); node.QueueFree(); }
        if (Part == null) return; var candidates = Part.Candidates(); _counts.Text = $"{Part.Selection.Selected.Count} selected / {candidates.Count} potential seams";
        foreach (var seam in candidates)
        {
            string id = seam.Id; var result = Part.Result(id); bool selected = Part.Selection.Selected.Contains(id), warning = Part.IsWarning(id);
            string suffix = warning ? result!.State == WeldSeamState.Warning ? $" · warning{(result.Partial ? $" {result.Coverage:P0}" : "")}" : " · blocked" : "";
            var button = Button(_list, $"{(selected ? "☑" : "☐")} {id} · {seam.Length * 1000:0.#} mm · {seam.AngleDegrees:0.#}°{suffix}", () => BeginSelection(id, _ui.GetGlobalMousePosition()));
            button.Alignment = HorizontalAlignment.Left; button.ClipText = true; button.TooltipText = warning ? result!.Reason : ""; button.Disabled = IsBusy || IsSimulating;
            button.AddThemeColorOverride("font_color", warning ? new Color("ff657b") : selected ? new Color("54e5df") : _muted);
            if (selected) button.AddThemeStyleboxOverride("normal", ButtonStyle(warning ? "392630" : "203c40", warning ? "9e4056" : "388b89"));
        }
        UpdateControls();
    }
    private void BeginBusy(string operation) { IsBusy = true; UpdateGizmo(); ClosePropagation(); _cancel = new(); _watch.Restart(); _percent = 0; _operation = operation; UpdateControls(); }
    private void EndBusy() { IsBusy = false; while (_messages.TryDequeue(out _)) { } _cancel?.Dispose(); _cancel = null; _watch.Stop(); if (!_exiting) { UpdateGizmo(); UpdateControls(); } }
    public void Generate() { if (IsBusy) return; PlanningTask = GenerateAsync(); }
    public async Task GenerateAsync()
    {
        if (IsBusy || IsSimulating) return; if (Part == null) { Toast?.Invoke("Import a CAD workpiece first"); return; }
        var selected = Part.AssignedSeams(); if (selected.Count == 0) { Toast?.Invoke("Select seams for the work assignment"); return; }
        Invalidate(true); BeginBusy("Preparing geometry"); int revision = _revision;
        try
        {
            var transform = _robot.GlobalTransform.AffineInverse() * Part.GlobalTransform;
            Vector3[] triangles = Part.Document.Indices.Select(i => transform * Part.Document.Vertices[i]).ToArray(); _robotTriangles ??= _robot.GetLinkTriangles();
            var rest = _controller.RestTransforms; var tool = _controller.ToolTransform; var start = _controller.AnglesDegrees;
            Vector3[] fixtures = new CylinderMesh { TopRadius = .70f, BottomRadius = .70f, Height = .201f, RadialSegments = 64 }.GetFaces().Select(p => p + new Vector3(0, -.1095f, 0)).ToArray();
            string name = Part.Document.Name; var token = _cancel!.Token; var weaving = SnapshotWeaving();
            var result = await Task.Run(() =>
            {
                _capsules ??= CollisionScene.CreateRobotCapsules(_robotTriangles).Concat(WeldTorch.CollisionVolumes()).ToArray();
                var collision = new CollisionScene(triangles, _capsules, rest, staticTriangles: fixtures, robotTriangles: _robotTriangles);
                var request = new WeldPlanRequest { Seams = selected, PartTransform = transform, JointRest = rest, ToolTransform = tool, StartAngles = start, Collision = collision, PartName = name, AllowWarningPaths = true };
                var program = WeldPlanner.Plan(request, token, (p, s) => _messages.Enqueue((p * 70, s)));
                program = WeavePlanner.Apply(program, request, weaving, token, (p, s) => _messages.Enqueue((70 + p * 10, s)));
                var post = RobotPostprocessor.Export(program, request, token, (p, s) => _messages.Enqueue((80 + p * 10, s)), new RobotPostprocessorOptions { Weaving = weaving, PositionToleranceMetres = weaving.Enabled ? .000025f : .0005f });
                var frames = WebBridge.BuildCollisionTimeline(program, post.PlaybackMotions, request, token, (p, s) => _messages.Enqueue((90 + p * 10, s)));
                return (program, request, post, frames, collision);
            }, token);
            if (_exiting || revision != _revision) return;
            Program = result.program; _request = result.request; _postprocessed = result.post; _playback = result.post.PlaybackMotions; _collisionFrames = result.frames; _collision = result.collision;
            foreach (var seam in Program.Seams) Part.SetResult(seam); Part.RefreshSeams(); RefreshList(); DrawPath();
            _status.Text = Program.Summary; _percent = 100; _operation = $"Program ready · {_watch.Elapsed.TotalSeconds:0.0}s"; Toast?.Invoke(Program.Summary);
        }
        catch (OperationCanceledException) { if (!_exiting) { _status.Text = "Generation cancelled"; _operation = "Generation cancelled"; } }
        catch (Exception error) { if (!_exiting) { _status.Text = "Generation failed · " + error.Message; _operation = "Generation failed"; Toast?.Invoke(error.Message); } }
        finally { EndBusy(); }
    }
    public void CancelGeneration() => _cancel?.Cancel();
    public void Run()
    {
        if (IsBusy || IsSimulating || Program == null || _playback.Length == 0) return;
        if (!_controller.CanMove) { Toast?.Invoke("Release emergency stop before simulation"); return; }
        if (_controller.AnglesDegrees.Zip(Program.StartAngles, (a, b) => Math.Abs(a - b)).Max() > .3f) { Toast?.Invoke("Robot start pose changed · generate again"); Invalidate(); return; }
        ClosePropagation(); Effects.Clear(); _motionIndex = 0; _elapsed = 0; _motionStart = _controller.AnglesDegrees; _controller.SetAutomaticMode(true);
        if (!_controller.ApplyPlannedPose(_motionStart, "Program ready")) { _controller.SetAutomaticMode(false); return; }
        IsSimulating = true; IsPaused = false; UpdateGizmo(); if (Part != null) Part.OverlaysVisible = false; if (_path != null) _path.Hide(); _hint.Hide(); UpdateControls();
    }
    public void Pause()
    {
        if (!IsSimulating) return; IsPaused = !IsPaused; Effects.SetArc(false, Vector3.Zero, Vector3.Up); _status.Text = IsPaused ? "AUTO · simulation paused" : "AUTO · simulation resumed"; UpdateControls();
    }
    public void Stop() { _revision++; if (IsBusy) _cancel?.Cancel(); FinishSimulation(); Program = null; _request = null; _postprocessed = null; _playback = Array.Empty<WeldMotion>(); ClearPath(); _status.Text = "Stopped · generate a new program"; UpdateControls(); }
    private void FinishSimulation()
    {
        if (IsSimulating) _controller.StopMotion(); IsSimulating = IsPaused = false; _controller?.SetAutomaticMode(false); Effects?.SetArc(false, Vector3.Zero, Vector3.Up);
        CollisionLinks = Array.Empty<int>(); CollisionPairs = Array.Empty<string>(); if (Part != null) Part.OverlaysVisible = true; if (_path != null) _path.Show(); UpdateGizmo();
    }
    public override void _PhysicsProcess(double delta)
    {
        if (!IsSimulating || Program == null) return;
        if (!_controller.CanMove || !_controller.IsPlannedMotion) { Stop(); return; }
        if (IsPaused) return; if (_motionIndex >= _playback.Length) { CompleteSimulation(); return; }
        var motion = _playback[_motionIndex]; _elapsed += (float)Math.Min(delta, .05) * _controller.SpeedOverride;
        float t = Mathf.Clamp(_elapsed / Math.Max(.001f, motion.DurationSeconds), 0, 1); var angles = new float[6]; for (int i = 0; i < 6; i++) angles[i] = Mathf.Lerp(_motionStart[i], motion.TargetAngles[i], t);
        var frame = _collisionFrames[_motionIndex].LastOrDefault(f => f.T <= t); CollisionLinks = frame?.Links ?? Array.Empty<int>(); CollisionPairs = frame?.Reasons ?? Array.Empty<string>();
        if (!_controller.ApplyPlannedPose(angles, motion.ArcOn ? "Welding · " + motion.SeamId : "Travel · " + motion.SeamId)) { Stop(); return; }
        var approach = _controller.TcpTransform.Basis.Y; Effects.SetArc(motion.ArcOn, _robot.GlobalTransform * (_controller.TcpPosition - approach * .003f), _robot.GlobalBasis * approach);
        _status.Text = CollisionPairs.Length > 0 ? string.Join(" · ", CollisionPairs) : $"{motion.Kind} · {motion.SeamId} · {_motionIndex + 1}/{_playback.Length}";
        if (t >= 1) { _motionIndex++; _elapsed = 0; _motionStart = angles; if (_motionIndex >= _playback.Length) CompleteSimulation(); }
    }
    private void CompleteSimulation() { FinishSimulation(); _status.Text = "Simulation complete · MANUAL"; UpdateControls(); }
    private void ClearPath() { if (_path == null) return; _path.QueueFree(); _path = null; }
    private void DrawPath()
    {
        ClearPath(); if (Program == null) return; _path = new Node3D { Name = "Calculated tool path" }; _robot.AddChild(_path);
        var weld = new List<Vector3>(); var travel = new List<Vector3>(); var rest = _controller.RestTransforms; float[] previousAngles = Program.StartAngles;
        Vector3 previous = RobotKinematics.Forward(previousAngles, rest, Program.ToolTransform).Origin; float dashDistance = 0;
        foreach (var motion in _playback)
        {
            int samples = Math.Max(1, (int)Math.Ceiling(previousAngles.Zip(motion.TargetAngles, (a, b) => Math.Abs(a - b)).Sum() / 2));
            for (int sample = 1; sample <= samples; sample++)
            {
                float t = sample / (float)samples; float[] pose = previousAngles.Zip(motion.TargetAngles, (a, b) => Mathf.Lerp(a, b, t)).ToArray();
                Vector3 next = RobotKinematics.Forward(pose, rest, Program.ToolTransform).Origin;
                if (motion.ArcOn) weld.AddRange(new[] { previous, next });
                else
                {
                    float length = previous.DistanceTo(next), offset = 0;
                    while (offset < length)
                    {
                        float span = Math.Min(length - offset, .012f - dashDistance % .012f); if (span < .000001f) span = Math.Min(.012f, length - offset);
                        if ((int)Math.Floor(dashDistance / .012f) % 2 == 0) travel.AddRange(new[] { previous.Lerp(next, offset / length), previous.Lerp(next, (offset + span) / length) });
                        dashDistance += span; offset += span;
                    }
                }
                previous = next;
            }
            previousAngles = motion.TargetAngles;
        }
        AddPathLines(weld, new Color("50eacf")); AddPathLines(travel, new Color("dfa85c"));
    }
    private void AddPathLines(List<Vector3> points, Color color)
    {
        if (points.Count == 0) return; var mesh = new ImmediateMesh(); mesh.SurfaceBegin(Mesh.PrimitiveType.Lines); foreach (var point in points) mesh.SurfaceAddVertex(point); mesh.SurfaceEnd();
        _path!.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, MaterialOverride = new StandardMaterial3D { AlbedoColor = color, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, EmissionEnabled = true, Emission = color } });
    }
    private void ShowWeaving()
    {
        if (IsBusy || IsSimulating) return; var dialog = new AcceptDialog { Title = "Weaving settings", MinSize = new Vector2I(390, 390) }; AddChild(dialog); dialog.GetOkButton().Text = "Done";
        var column = Column(dialog, 8); var illustration = new WeavingIllustration { Settings = Weaving }; column.AddChild(illustration);
        (string Title, float Value, float Min, float Max, Action<float> Update)[] settings = {
            ("Amplitude ± mm", Weaving.AmplitudeMm, .1f, 10, v => Weaving.AmplitudeMm = v), ("Frequency · Hz", Weaving.FrequencyHz, .1f, 5, v => Weaving.FrequencyHz = v),
            ("Plane angle · °", Weaving.AngleDegrees, -180, 180, v => Weaving.AngleDegrees = v), ("Smooth ends · mm", Weaving.FadeMm, .5f, 20, v => Weaving.FadeMm = v) };
        foreach (var setting in settings) { var row = Row(column); Text(row, setting.Title, 13, _ink).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; var spin = Spin(row, setting.Value, setting.Min, setting.Max, .1f); spin.ValueChanged += value => { setting.Update((float)value); illustration.QueueRedraw(); Invalidate(true); }; }
        dialog.Confirmed += dialog.QueueFree; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new Vector2I(400, 390));
    }
    private WeavingOptions SnapshotWeaving() => new() { Enabled = Weaving.Enabled, AmplitudeMm = Weaving.AmplitudeMm, FrequencyHz = Weaving.FrequencyHz, AngleDegrees = Weaving.AngleDegrees, FadeMm = Weaving.FadeMm };
    private void Export()
    {
        if (Program == null || _request == null || IsBusy || IsSimulating) return;
        var dialog = new ConfirmationDialog { Title = "Export Lua program", MinSize = new Vector2I(360, 160) }; AddChild(dialog); dialog.GetOkButton().Text = "Save";
        var column = Column(dialog, 9); var linearize = new CheckBox { Text = "Convert arcs to linear moves", ButtonPressed = false }; column.AddChild(linearize);
        var row = Row(column); Text(row, "Torch digital output", 13, _ink).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill; var output = Spin(row, 1, 0, 65535);
        dialog.Confirmed += () => { bool lines = linearize.ButtonPressed; int io = (int)output.Value; dialog.QueueFree(); ChooseExportFile(lines, io); }; dialog.Canceled += dialog.QueueFree; dialog.PopupCentered(new Vector2I(370, 160));
    }
    private void ChooseExportFile(bool linearize, int io)
    {
        var dialog = new FileDialog { Title = "Save Lua program", Access = FileDialog.AccessEnum.Filesystem, FileMode = FileDialog.FileModeEnum.SaveFile, UseNativeDialog = true, CurrentFile = "ency-hyper-estun.lua", Filters = new[] { "*.lua ; Lua program" } }; AddChild(dialog);
        dialog.FileSelected += async path => { dialog.QueueFree(); await SaveLuaAsync(path, linearize, io); }; dialog.Canceled += dialog.QueueFree; dialog.PopupCenteredRatio(.7f);
    }
    public async Task SaveLuaAsync(string path, bool linearize = false, int io = 1)
    {
        if (Program == null || _request == null || IsBusy || IsSimulating) return; var program = Program; var request = _request; var weaving = SnapshotWeaving(); BeginBusy("Preparing Lua export");
        try
        {
            var token = _cancel!.Token;
            var exported = await Task.Run(() =>
            {
                var post = RobotPostprocessor.Export(program, request, token, (p, s) => _messages.Enqueue((p * 80, s)), new RobotPostprocessorOptions { LinearizeArcs = linearize, TorchDigitalOutput = io, Weaving = weaving, PositionToleranceMetres = weaving.Enabled ? .000025f : .0005f });
                var frames = WebBridge.BuildCollisionTimeline(program, post.PlaybackMotions, request, token, (p, s) => _messages.Enqueue((80 + p * 20, s)));
                return (post, frames);
            }, token);
            token.ThrowIfCancellationRequested(); if (!path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase)) path += ".lua"; await System.IO.File.WriteAllTextAsync(path, exported.post.Text, token);
            if (!_exiting) { _postprocessed = exported.post; _playback = exported.post.PlaybackMotions; _collisionFrames = exported.frames; DrawPath(); }
            if (!_exiting) { Toast?.Invoke("Lua program saved · " + Path.GetFileName(path)); _operation = "Lua program saved"; _percent = 100; }
        }
        catch (OperationCanceledException) { if (!_exiting) _operation = "Export cancelled"; }
        catch (Exception error) { if (!_exiting) { _operation = "Export failed"; Toast?.Invoke(error.Message); } }
        finally { EndBusy(); }
    }
    private void UpdateControls()
    {
        if (_plan == null) return; bool edit = !IsBusy && !IsSimulating;
        _plan.Disabled = !edit || Part == null || Part.Selection.Selected.Count == 0; _plan.Text = IsBusy ? $"Generating · {_percent:0}%" : "Generate program";
        _cancelButton.Visible = IsBusy; _run.Disabled = !edit || Program == null || _playback.Length == 0;
        _pause.Disabled = !IsSimulating; _pause.Text = IsPaused ? "Resume" : "Pause"; _stop.Disabled = !IsSimulating; _export.Visible = Program != null && _playback.Length > 0; _export.Disabled = !edit;
        foreach (var button in _editingButtons) button.Disabled = !edit; _weave.Disabled = _concave.Disabled = !edit;
        foreach (var spin in _pose.Concat(new[] { _min, _max, _minLength, _maxLength })) spin.Editable = edit;
        _angles.MouseFilter = edit ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore; _lengthLow.Editable = _lengthHigh.Editable = edit;
        foreach (var button in _list.GetChildren().OfType<Button>()) button.Disabled = !edit;
    }
    public override void _Process(double delta)
    {
        while (_messages.TryDequeue(out var message)) { if (IsBusy) { if (message.Percent >= 0) _percent = message.Percent; _operation = message.Text; } }
        if (IsBusy) { _plan.Text = $"Generating · {_percent:0}%"; _progress.Text = $"{_percent:0}% · {_operation} · {_watch.Elapsed.TotalSeconds:0.0}s"; _progressBar.Value = _percent; }
        else if (_operation.Length > 0) { _progress.Text = _operation; _progressBar.Value = _percent; }
        if (IsSimulating || IsBusy) _gizmo.Active = false;
        if (Part != null && !IsSimulating && !IsBusy && !_propagation.Visible && !_gizmo.Dragging)
        {
            Vector2 mouse = _ui.GetGlobalMousePosition(); bool overPanel = _panel.Visible && _panel.GetGlobalRect().HasPoint(mouse); string hint = overPanel ? "" : Part.WarningHint(Part.SeamAt(mouse));
            _hint.Visible = hint.Length > 0; if (_hint.Visible) { _hintText.Text = hint; _hint.Position = new Vector2(Mathf.Clamp(mouse.X + 14, 4, Math.Max(4, _ui.Size.X - _hint.Size.X - 4)), Mathf.Clamp(mouse.Y + 18, 4, Math.Max(4, _ui.Size.Y - _hint.Size.Y - 4))); }
        }
        else _hint.Hide();
    }
    public override void _Input(InputEvent input) { if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click && _propagation.Visible && !_propagation.GetGlobalRect().HasPoint(click.Position)) ClosePropagation(); }
    public override void _UnhandledInput(InputEvent input)
    {
        if (Part == null || IsBusy || IsSimulating) return;
        if (input is InputEventKey { Pressed: true, Echo: false } key) { if (key.Keycode == Key.W) SetGizmoMode(false); if (key.Keycode == Key.E) SetGizmoMode(true); }
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            if (_gizmo.TryBeginDrag(click.Position)) { GetViewport().SetInputAsHandled(); return; }
            string? id = Part.SeamAt(click.Position); if (id != null) { BeginSelection(id, click.Position); GetViewport().SetInputAsHandled(); }
        }
    }
    public override void _ExitTree() { _exiting = true; _cancel?.Cancel(); }
    private static VBoxContainer Column(Node parent, int spacing) { var result = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; result.AddThemeConstantOverride("separation", spacing); parent.AddChild(result); return result; }
    private static HBoxContainer Row(Control parent) { var result = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; result.AddThemeConstantOverride("separation", 5); parent.AddChild(result); return result; }
    private Label Text(Control parent, string text, int size, Color color) { var result = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore }; result.AddThemeFontSizeOverride("font_size", size); result.AddThemeColorOverride("font_color", color); parent.AddChild(result); return result; }
    private HSlider Slider(Control parent, double min, double max, double value, double step) { var result = new HSlider { MinValue = min, MaxValue = max, Value = value, Step = step, CustomMinimumSize = new Vector2(0, 15), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }; parent.AddChild(result); return result; }
    private SpinBox Spin(Control parent, float value, float min, float max, float step = 1)
    {
        var result = new SpinBox { CustomMinimumSize = new Vector2(70, 27), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MinValue = min, MaxValue = max, Step = step, Value = value }; parent.AddChild(result); result.AddThemeFontSizeOverride("font_size", 12); result.GetLineEdit().AddThemeFontSizeOverride("font_size", 12); return result;
    }
    private Button EditButton(Control parent, string text, Action action, bool expand = true) { var button = Button(parent, text, action, expand); _editingButtons.Add(button); return button; }
    private Button Button(Control parent, string text, Action action, bool expand = true)
    {
        var result = new Button { Text = text, CustomMinimumSize = new Vector2(0, 28), SizeFlagsHorizontal = expand ? Control.SizeFlags.ExpandFill : Control.SizeFlags.Fill, FocusMode = Control.FocusModeEnum.None }; result.AddThemeFontSizeOverride("font_size", 12); result.AddThemeColorOverride("font_color", _ink);
        result.AddThemeStyleboxOverride("normal", ButtonStyle("29343d", "394650")); result.AddThemeStyleboxOverride("hover", ButtonStyle("364550", "526472")); result.AddThemeStyleboxOverride("pressed", ButtonStyle("34534f", "54cfc8")); result.AddThemeStyleboxOverride("disabled", ButtonStyle("252d34", "313a42")); parent.AddChild(result); result.Pressed += action; return result;
    }
    private static StyleBoxFlat ButtonStyle(string background, string border) => new() { BgColor = new Color(background), BorderColor = new Color(border), BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5, CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, ContentMarginLeft = 7, ContentMarginRight = 7, ContentMarginTop = 4, ContentMarginBottom = 4 };
}
