using Godot;
using System;
using System.Globalization;

namespace EstunStudio;

/// <summary>A native-pixel pendant with responsive containers and hold-to-jog controls.</summary>
public partial class PendantPanel : Control
{
    public event Action<string>? Toast;
    public event Action? HomeViewRequested;
    public event Action? CloseRequested;
    private enum JogFrame { Joint, World, Tool }
    private static readonly Color Ink = new("#e7ecef"), Muted = new("#99a6ae");
    private static readonly Color Orange = new("#eaa05b"), Green = new("#86c9a6");
    private static readonly Color Surface = new("#222a30"), Border = new("#39434b");
    private static readonly Color ScreenInk = new("#293932"), ScreenMuted = new("#66756d");
    private static readonly string[] JointNames = { "J1", "J2", "J3", "J4", "J5", "J6" };
    private static readonly string[] JointCaptions = { "Base", "Shoulder", "Elbow", "Wrist 1", "Wrist 2", "Flange" };
    private static readonly string[] TcpNames = { "X", "Y", "Z", "A", "B", "C" };
    private static readonly string[] TcpCaptions = { "Position X", "Position Y", "Position Z", "Roll", "Pitch", "Yaw" };
    private RobotController? _controller;
    private Font _font = null!, _semibold = null!;
    private readonly Label[] _axisNames = new Label[6], _axisValues = new Label[6], _axisUnits = new Label[6];
    private readonly Button[] _rowSelectors = new Button[6], _tabs = new Button[3];
    private Label _frameDescription = null!, _speedValue = null!, _statusLabel = null!, _waypointLabel = null!;
    private Label _drivesCaption = null!, _runCaption = null!, _motionHint = null!;
    private Button _drivesButton = null!, _emergencyButton = null!;
    private HSlider _speedSlider = null!;
    private VBoxContainer _content = null!;
    private VBoxContainer _screen = null!;
    private MushroomStopButton _mushroom = null!;
    private JogFrame _frame;
    private int _selectedAxis, _keyboardJogDirection, _pointerJogAxis = -1;
    private bool _lastEstop, _lastDrives;
    private float _readoutTimer;

    public void Build(RobotController controller)
    {
        _controller = controller;
        Name = "TeachPendant";
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(320, 0);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _font = ResourceLoader.Exists("res://Assets/Fonts/Inter-Regular.ttf") ? GD.Load<Font>("res://Assets/Fonts/Inter-Regular.ttf") : ThemeDB.FallbackFont;
        _semibold = new FontVariation { BaseFont=_font, VariationOpentype=new Godot.Collections.Dictionary { ["wght"]=650 } };
        AddThemeFontOverride("font", _font);
        AddThemeFontSizeOverride("font_size", 14);
        var surface = new PanelContainer { Name = "PendantSurface", MouseFilter = MouseFilterEnum.Stop };
        AddChild(surface);
        surface.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        surface.AddThemeStyleboxOverride("panel",new StyleBoxEmpty());
        var shell=new PendantShell {Name="MoldedEnclosure"};AddChild(shell);MoveChild(shell,0);shell.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var padding = new MarginContainer { MouseFilter = MouseFilterEnum.Pass };
        SetMargins(padding, 18);
        surface.AddChild(padding);
        _content = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _content.AddThemeConstantOverride("separation", 8);
        padding.AddChild(_content);
        BuildHeader();
        var bezel=new PanelContainer {Name="RecessedTouchscreen"};_content.AddChild(bezel);
        var bezelStyle=Flat(new Color("#0d1518"),9,new Color("#6b726c"),1);
        bezelStyle.ContentMarginLeft=3;bezelStyle.ContentMarginRight=3;bezelStyle.ContentMarginTop=3;bezelStyle.ContentMarginBottom=4;
        bezel.AddThemeStyleboxOverride("panel",bezelStyle);
        var glass=new PanelContainer();bezel.AddChild(glass);
        var glassStyle=Flat(new Color("#e2e8df"),6,new Color("#a9b9a9"),1);
        glassStyle.ContentMarginLeft=7;glassStyle.ContentMarginRight=7;glassStyle.ContentMarginTop=7;glassStyle.ContentMarginBottom=7;
        glass.AddThemeStyleboxOverride("panel",glassStyle);
        _screen=new VBoxContainer();_screen.AddThemeConstantOverride("separation",6);glass.AddChild(_screen);
        var screenHeader=Row(_screen,4);
        _statusLabel=MakeLabel(screenHeader,"Drives off",12,ScreenMuted,true);_statusLabel.SizeFlagsHorizontal=SizeFlags.ExpandFill;
        MakeLabel(screenHeader,"T1  /  MANUAL",12,ScreenMuted);
        BuildJogControls();
        BuildSpeedControl();
        BuildHardwareKeys();
        SelectFrame(JogFrame.Joint);
        RefreshState();
        // The parent owns position and available width; fonts never inherit a canvas scale.
        _content.MinimumSizeChanged += UpdateMinimumHeight;
        UpdateMinimumHeight();
    }

    private void UpdateMinimumHeight() => CustomMinimumSize = new Vector2(320, Mathf.Ceil(_content.GetCombinedMinimumSize().Y + 38));

    private void BuildHeader()
    {
        var header = Row(_content, 8);
        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titles.AddThemeConstantOverride("separation", 2);
        header.AddChild(titles);
        MakeLabel(titles, "ESTUN", 24, new Color("#f0f1eb"), true);
        MakeLabel(titles, "TEACH PENDANT", 12, new Color("#b4bbb6"));
        var detail=Row(titles,6);
        MakeLabel(detail, "S20-180 PRO", 12, Muted).SizeFlagsHorizontal=SizeFlags.ExpandFill;
        var close = MakeButton(detail, "Hide", 46, 25);
        close.AddThemeFontSizeOverride("font_size",12);
        close.Name = "HidePendant";
        close.TooltipText = "Hide the pendant and stop motion. Reopen it from the top toolbar.";
        close.Pressed += () => { _pointerJogAxis = -1; _keyboardJogDirection = 0; _controller!.StopMotion(); CloseRequested?.Invoke(); };
        _mushroom=new MushroomStopButton {CustomMinimumSize=new Vector2(80,80),SizeFlagsVertical=SizeFlags.ShrinkCenter,FocusMode=FocusModeEnum.None};header.AddChild(_mushroom);
        _emergencyButton=_mushroom;
        _emergencyButton.Name = "EmergencyStop";
        _emergencyButton.TooltipText = "Emergency stop · Escape\nStops motion and disables drives. Press RESET to clear the latch.";
        _emergencyButton.Pressed += () => { EmergencyStop(); Toast?.Invoke("Emergency stop · Press RESET, then enable DRIVES."); };
    }

    private void BuildJogControls()
    {
        var tabs = Row(_screen, 4);
        string[] titles = { "JOINT", "WORLD", "TOOL" };
        string[] tips =
        {
            "Jog J1–J6 independently. Hold − / + or Left / Right. Up / Down selects an axis.",
            "Jog TCP along world axes. Y is vertical. X / Y / Z are millimeters; A / B / C use YXZ Euler angles.",
            "Jog along local tool axes. TCP readouts remain in the robot base frame; rotations use YXZ Euler angles."
        };
        for (int i = 0; i < 3; i++)
        {
            int frame = i;
            _tabs[i] = MakeButton(tabs, titles[i], 0, 32);
            _tabs[i].SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _tabs[i].TooltipText = tips[i];
            _tabs[i].Pressed += () => SelectFrame((JogFrame)frame);
        }
        _frameDescription = MakeLabel(_screen, "Axis position · degrees", 12, ScreenMuted);
        var rows = new VBoxContainer { Name = "AxisRows" };
        rows.AddThemeConstantOverride("separation", 5);
        _screen.AddChild(rows);
        for (int i = 0; i < 6; i++)
        {
            int axis = i;
            var row = Row(rows, 5);
            row.Name = $"AxisRow{i + 1}";
            var selector = MakeButton(row, "", 0, 42);
            selector.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            selector.Name = $"SelectAxis{i + 1}";
            _rowSelectors[i] = selector;
            selector.Pressed += () => SelectAxis(axis);
            var inset = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            selector.AddChild(inset);
            inset.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            SetMargins(inset, 8);
            inset.AddThemeConstantOverride("margin_top", 0);
            inset.AddThemeConstantOverride("margin_bottom", 0);
            var readout = Row(inset, 3);
            readout.MouseFilter = MouseFilterEnum.Ignore;
            _axisNames[i] = MakeLabel(readout, JointNames[i], 14, ScreenInk, true);
            _axisNames[i].CustomMinimumSize = new Vector2(25, 0);
            _axisValues[i] = MakeLabel(readout, "0.00", 16, ScreenInk);
            _axisValues[i].SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _axisValues[i].HorizontalAlignment = HorizontalAlignment.Right;
            _axisValues[i].CustomMinimumSize = new Vector2(68, 0);
            _axisUnits[i] = MakeLabel(readout, "°", 12, ScreenMuted);
            _axisUnits[i].CustomMinimumSize = new Vector2(20, 0);
            _axisUnits[i].HorizontalAlignment = HorizontalAlignment.Right;
            Button minus = PhysicalButton(row, "−", 48, 42), plus = PhysicalButton(row, "+", 48, 42);
            minus.AddThemeFontSizeOverride("font_size", 22);
            plus.AddThemeFontSizeOverride("font_size", 22);
            BindJog(minus, axis, -1); BindJog(plus, axis, 1);
        }
    }

    private void BuildSpeedControl()
    {
        var group = new VBoxContainer();
        group.AddThemeConstantOverride("separation", 2);
        _screen.AddChild(group);
        var title = Row(group, 8);
        MakeLabel(title, "Speed override", 12, ScreenMuted).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _speedValue = MakeLabel(title, "25%", 14, ScreenInk, true);
        _speedSlider = new HSlider
        {
            Name = "SpeedOverride", MinValue = 1, MaxValue = 100, Step = 1,
            Value = _controller!.SpeedOverride * 100, CustomMinimumSize = new Vector2(0, 28),
            FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "Manual jogging and program playback speed: 1–100%."
        };
        StyleBoxFlat track = Flat(new Color("#bac8b9"), 2);
        track.ContentMarginTop = track.ContentMarginBottom = 2;
        StyleBoxFlat fill = Flat(new Color("#526e60"), 2);
        fill.ContentMarginTop = fill.ContentMarginBottom = 2;
        _speedSlider.AddThemeStyleboxOverride("slider", track);
        _speedSlider.AddThemeStyleboxOverride("grabber_area", fill);
        _speedSlider.AddThemeStyleboxOverride("grabber_area_highlight", fill);
        _speedSlider.AddThemeIconOverride("grabber", MakeSliderKnob(false));
        _speedSlider.AddThemeIconOverride("grabber_highlight", MakeSliderKnob(true));
        group.AddChild(_speedSlider);
        _speedSlider.ValueChanged += value => _controller!.SpeedOverride = (float)value / 100;
    }

    private void BuildHardwareKeys()
    {
        var power = Row(_content, 8);
        _drivesButton = HardwareButton(power, "DRIVES OFF", out _drivesCaption);
        _drivesButton.Name = "Drives";
        _drivesButton.TooltipText = "Enable or disable drives. Enable DRIVES, then hold a jog control or click HOME / RUN.";
        _drivesButton.Pressed += () =>
        {
            _controller!.SetDrives(!_controller.DrivesEnabled);
            RefreshState();
            Toast?.Invoke(_controller.EmergencyStopped ? "Emergency stop is latched. Press RESET first." : _controller.DrivesEnabled ? "Drives enabled · Hold − / + to jog, or click HOME / RUN." : "Drives off · Motion stopped.");
        };
        var stop = HardwareButton(power, "STOP MOTION", out _);
        stop.Name = "StopMotion";
        ((PhysicalKeyButton)stop).Accent=Orange;
        stop.TooltipText = "Stop every robot motion · Space\nDrives remain enabled.";
        stop.ButtonDown += StopMotion;
        var programHeader = Row(_content, 8);
        MakeLabel(programHeader, "Motion program", 12, Muted).SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _waypointLabel = MakeLabel(programHeader, "0 points", 12, Muted);
        var program = Row(_content, 5);
        string[] titles = { "HOME", "REC", "RUN", "RESET" };
        string[] hints =
        {
            "Move to the home pose. Enable DRIVES, then click HOME. Space stops motion.",
            "Record and save the current pose. Right-click to save the program again.",
            "Run recorded points. Click again or press Space to stop.",
            "Clear the emergency stop latch. Drives remain off. Right-click to clear the program."
        };
        for (int i = 0; i < titles.Length; i++)
        {
            Button button = HardwareButton(program, titles[i], out Label caption, 36);
            button.Name = titles[i] == "REC" ? "Record" : titles[i];
            button.TooltipText = hints[i];
            if (i == 0) button.Pressed += () =>
            {
                if (!RequireMotionPermission()) return;
                _controller!.GoHome(); HomeViewRequested?.Invoke(); Toast?.Invoke("Returning home · Press Space to stop.");
            };
            if (i == 1)
            {
                button.Pressed += () =>
                {
                    if (!_controller!.RecordWaypoint()) { Toast?.Invoke(_controller.Status); return; }
                    bool saved = _controller.SaveWaypoints();
                    Toast?.Invoke(saved ? $"Point P{_controller.Waypoints.Count:000} recorded and saved." : _controller.Status);
                    RefreshReadouts();
                };
                button.GuiInput += input =>
                {
                    if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                        Toast?.Invoke(_controller!.SaveWaypoints() ? "Robot program saved." : _controller.Status);
                };
            }
            if (i == 2)
            {
                _runCaption = caption;
                button.Pressed += () =>
                {
                    if (_controller!.IsPlaying || _controller.MotionActive) { StopMotion(); return; }
                    if (!RequireMotionPermission()) return;
                    if (_controller.Waypoints.Count < 2) { Toast?.Invoke("Record at least two points to run a program."); return; }
                    _controller.PlayWaypoints(); Toast?.Invoke("Program running · Press Space to stop.");
                };
            }
            if (i == 3)
            {
                button.Pressed += () =>
                {
                    _pointerJogAxis = -1; _keyboardJogDirection = 0; _controller!.StopMotion();
                    _controller.ResetEmergencyStop(); RefreshState(); Toast?.Invoke("Controller reset · Enable DRIVES to continue.");
                };
                button.GuiInput += input =>
                {
                    if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                    {
                        _controller!.ClearWaypoints(); _controller.SaveWaypoints();
                        Toast?.Invoke("Motion program cleared."); RefreshReadouts();
                    }
                };
            }
        }
        _motionHint = MakeLabel(_content, "SPACE  STOP   ·   ESC  E-STOP", 12, Muted);
        _motionHint.HorizontalAlignment=HorizontalAlignment.Center;
    }

    public override void _Input(InputEvent input)
    {
        if (_controller is null) return;
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            StopPointerJog();
            return;
        }
        if (input is not InputEventKey key || key.Echo) return;
        Key code = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        // Input callbacks still run when an ancestor hides this control (cinema mode).
        // Stop shortcuts remain global; invisible jog controls never start motion.
        if (!IsVisibleInTree() && key.Pressed && code is not (Key.Escape or Key.Space)) return;
        if (code == Key.Space)
        {
            if (key.Pressed) StopMotion();
            GetViewport().SetInputAsHandled();
        }
        else if (code == Key.Escape && key.Pressed)
        {
            EmergencyStop();
            Toast?.Invoke("EMERGENCY STOP · Press RESET, then enable DRIVES.");
            GetViewport().SetInputAsHandled();
        }
        else if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit or Godot.Range) return;
        else if (code is Key.Up or Key.Down && key.Pressed)
        {
            SelectAxis((_selectedAxis + (code == Key.Up ? 5 : 1)) % 6);
            GetViewport().SetInputAsHandled();
        }
        else if (code is Key.Left or Key.Right)
        {
            int direction = code == Key.Left ? -1 : 1;
            if (key.Pressed)
            {
                _keyboardJogDirection = direction;
                BeginJog(_selectedAxis, direction, false);
            }
            else if (_keyboardJogDirection == direction)
            {
                _keyboardJogDirection = 0;
                _controller.StopJog();
            }
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (_controller is null) return;


        // Window focus changes and releases outside any button must never leave a jog latched.
        if (_pointerJogAxis >= 0 && !Input.IsMouseButtonPressed(MouseButton.Left)) StopPointerJog();
        if (_keyboardJogDirection != 0 && !Input.IsPhysicalKeyPressed(_keyboardJogDirection < 0 ? Key.Left : Key.Right))
        {
            _keyboardJogDirection = 0;
            _controller.StopJog();
        }
        if (_lastEstop != _controller.EmergencyStopped || _lastDrives != _controller.DrivesEnabled)
        {
            RefreshState();
            QueueRedraw();
        }
        if (!IsVisibleInTree()) return;
        _readoutTimer += (float)delta;
        if (_readoutTimer < 1f / 20f) return;
        _readoutTimer = 0;
        RefreshReadouts();
        if (_controller.EmergencyStopped) QueueRedraw();
    }

    public override void _ExitTree()
    {
        if (_controller is null || !GodotObject.IsInstanceValid(_controller)) return;
        _controller.StopMotion();
    }

    public override void _Notification(int what)
    {
        if (_controller is null || !GodotObject.IsInstanceValid(_controller)) return;
        if (what != NotificationApplicationFocusOut && !(what == NotificationVisibilityChanged && !IsVisibleInTree())) return;
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _controller.StopMotion();
    }

    private void SelectFrame(JogFrame frame)
    {
        _controller!.StopJog();
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _frame = frame;
        for (int i = 0; i < 3; i++)
        {
            bool active = i == (int)frame;
            _tabs[i].AddThemeStyleboxOverride("normal", Flat(active ? new Color("#425d50") : new Color("#d3ddcf"), 4, active ? new Color("#425d50") : new Color("#bac9b9"), 1));
            _tabs[i].AddThemeStyleboxOverride("hover", Flat(active ? new Color("#567261") : new Color("#c1d2bc"), 4));
            _tabs[i].AddThemeColorOverride("font_color", active ? new Color("#f2f5ee") : ScreenMuted);
            _tabs[i].AddThemeColorOverride("font_hover_color", active ? Colors.White : ScreenInk);
        }
        _frameDescription.Text = frame switch
        {
            JogFrame.Joint => "Axis position · degrees",
            JogFrame.World => "TCP in base frame · world jog",
            _ => "TCP in base frame · tool-relative jog",
        };
        for (int i = 0; i < 6; i++)
        {
            _axisNames[i].Text = frame == JogFrame.Joint ? JointNames[i] : TcpNames[i];
            _rowSelectors[i].TooltipText = (frame == JogFrame.Joint ? JointCaptions[i] : TcpCaptions[i]) + " · Select this axis; hold − / + to jog.";
            _axisUnits[i].Text = frame == JogFrame.Joint || i >= 3 ? "°" : "mm";
        }
        SelectAxis(_selectedAxis);
        RefreshReadouts();
    }

    private void SelectAxis(int axis)
    {
        if (_selectedAxis != axis)
        {
            _controller!.StopJog();
            _keyboardJogDirection = 0;
        }
        _selectedAxis = axis;
        for (int i = 0; i < 6; i++)
        {
            bool active = i == axis;
            _rowSelectors[i].AddThemeStyleboxOverride("normal", Flat(active ? new Color("#cedfcb") : new Color("#eaf0e5"), 4, active ? new Color("#99b594") : new Color("#c4d0c0"), 1));
            _rowSelectors[i].AddThemeStyleboxOverride("hover",Flat(new Color("#d9e6d2"),4,new Color("#9db396"),1));
            _rowSelectors[i].AddThemeStyleboxOverride("pressed",Flat(new Color("#bdcfb5"),4));
            _axisNames[i].AddThemeColorOverride("font_color", active ? new Color("#346545") : ScreenInk);
        }
    }

    private void BindJog(Button button, int axis, int direction)
    {
        button.TooltipText = $"Axis {axis + 1}: {(direction > 0 ? "positive" : "negative")} direction.\nEnable DRIVES, then hold this button. Release or move the pointer outside it to stop jogging.";
        button.ButtonDown += () => BeginJog(axis, direction, true);
        button.ButtonUp += StopPointerJog;
        button.MouseExited += () =>
        {
            if (_pointerJogAxis == axis) StopPointerJog();
        };
    }

    private void BeginJog(int axis, int direction, bool pointer)
    {
        if (!IsVisibleInTree()) return;
        SelectAxis(axis);
        if (!RequireMotionPermission()) return;
        if (pointer) _pointerJogAxis = axis;
        if (_frame == JogFrame.Joint) _controller!.SetJointJog(axis, direction);
        else _controller!.SetTcpJog(axis, direction, _frame == JogFrame.Tool);
    }

    private void StopPointerJog()
    {
        if (_pointerJogAxis < 0) return;
        _pointerJogAxis = -1;
        _controller?.StopJog();
    }

    private bool RequireMotionPermission()
    {
        if (_controller!.EmergencyStopped)
        {
            Toast?.Invoke("Emergency stop is latched · Press RESET, then enable DRIVES.");
            return false;
        }
        if (!_controller.DrivesEnabled)
        {
            Toast?.Invoke("Enable DRIVES to move. Hold − / + to jog, or click HOME / RUN.");
            return false;
        }
        return true;
    }

    private void StopMotion()
    {
        if (_controller is null) return;
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _controller.StopMotion();
        Toast?.Invoke("Motion stopped.");
        RefreshState();
        QueueRedraw();
    }

    private void EmergencyStop()
    {
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _controller!.EmergencyStop();
        RefreshState();
        QueueRedraw();
    }

    private void RefreshState()
    {
        if (_controller is null || _drivesCaption is null) return;
        _lastEstop = _controller.EmergencyStopped;
        _lastDrives = _controller.DrivesEnabled;
        _mushroom.Latched=_lastEstop;
        _drivesCaption.Text = _lastDrives ? "DRIVES ON" : "DRIVES OFF";
        _drivesCaption.AddThemeColorOverride("font_color", _lastDrives ? Green : new Color("#d8dfda"));
        if(_drivesButton is PhysicalKeyButton key){key.Accent=Green;key.Illuminated=_lastDrives;}
        _motionHint.Text = _lastEstop ? "E-STOP ACTIVE  ·  PRESS RESET" : "SPACE  STOP   ·   ESC  E-STOP";
        _motionHint.AddThemeColorOverride("font_color", _lastEstop ? new Color("#f09b87") : Muted);
        RefreshReadouts();
    }

    private void RefreshReadouts()
    {
        if (_controller is null || _speedValue is null) return;
        if (_frame == JogFrame.Joint)
        {
            float[] angles = _controller.AnglesDegrees;
            for (int i = 0; i < Math.Min(6, angles.Length); i++)
                _axisValues[i].Text = Format(angles[i], 2);
        }
        else
        {
            Transform3D tcp = _controller.TcpTransform;
            Vector3 p = tcp.Origin * 1000;
            Vector3 e = tcp.Basis.GetEuler() * (180f / Mathf.Pi);
            float[] values = { p.X, p.Y, p.Z, e.X, e.Y, e.Z };
            for (int i = 0; i < 6; i++) _axisValues[i].Text = Format(values[i], i < 3 ? 1 : 2);
        }
        _speedValue.Text = Math.Round(_controller.SpeedOverride * 100).ToString(CultureInfo.InvariantCulture) + "%";
        string status = _controller.EmergencyStopped ? "Emergency stopped" : !_controller.DrivesEnabled ? "Drives off" : !_controller.LastIkSucceeded ? "TCP unreachable" : _controller.Status.Contains("limit", StringComparison.OrdinalIgnoreCase) ? "Joint limit reached" : _controller.IsPlaying ? "Program running" : _controller.MotionActive ? "Motion active" : "Ready to move";
        _statusLabel.Text = status;
        _statusLabel.AddThemeColorOverride("font_color", _controller.EmergencyStopped ? new Color("#a74232") : !_controller.LastIkSucceeded ? new Color("#946827") : _controller.DrivesEnabled ? new Color("#356d48") : ScreenMuted);
        _waypointLabel.Text = $"{_controller.Waypoints.Count} points";
        _runCaption.Text = _controller.IsPlaying || _controller.MotionActive ? "STOP" : "RUN";
    }

    private static string Format(float value, int decimals)
    {
        if (Math.Abs(value) < (decimals == 2 ? .005f : .05f)) value = 0;
        return value.ToString(decimals == 2 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
    }

    private static HBoxContainer Row(Node parent, int spacing)
    {
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        row.AddThemeConstantOverride("separation", spacing); parent.AddChild(row); return row;
    }

    private Label MakeLabel(Node parent, string text, int size, Color color, bool bold = false)
    {
        var label = new Label { Text = text, VerticalAlignment = VerticalAlignment.Center, MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", bold ? _semibold : _font);
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        parent.AddChild(label); return label;
    }

    private Button MakeButton(Node parent, string text, float width, float height)
    {
        var button = new Button { Text = text, CustomMinimumSize = new Vector2(width, height), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
        button.AddThemeFontOverride("font", _semibold);
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Colors.White);
        button.AddThemeStyleboxOverride("normal", Flat(Surface, 6, Border, 1));
        button.AddThemeStyleboxOverride("hover", Flat(new Color("#35434c"), 6, new Color("#667d89"), 1));
        button.AddThemeStyleboxOverride("pressed", Flat(new Color("#4b463c"), 6, Orange, 1));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        parent.AddChild(button); return button;
    }

    private Button HardwareButton(Node parent, string text, out Label caption, float height = 40)
    {
        var button = PhysicalButton(parent, "", 0, height);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        caption = MakeLabel(button, text, 12, Ink, true);
        caption.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        return button;
    }
    private Button PhysicalButton(Node parent,string text,float width,float height)
    {
        var button=new PhysicalKeyButton {Text=text,CustomMinimumSize=new Vector2(width,height),FocusMode=FocusModeEnum.None,MouseDefaultCursorShape=CursorShape.PointingHand};
        button.AddThemeFontOverride("font",_semibold);button.AddThemeFontSizeOverride("font_size",12);parent.AddChild(button);return button;
    }

    private static void SetMargins(MarginContainer container, int margin)
    {
        foreach (string side in new[] { "left", "right", "top", "bottom" }) container.AddThemeConstantOverride("margin_" + side, margin);
    }

    private static StyleBoxFlat Flat(Color color, int radius, Color? border = null, int borderWidth = 0)
    {
        return new StyleBoxFlat
        {
            BgColor = color, BorderColor = border ?? color,
            BorderWidthLeft = borderWidth, BorderWidthTop = borderWidth, BorderWidthRight = borderWidth, BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius, CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            CornerDetail = 8, AntiAliasing = true,
        };
    }

    private static Texture2D MakeSliderKnob(bool hover)
    {
        const int size = 18;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        Vector2 center = new(8.5f, 8.5f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float r = new Vector2(x, y).DistanceTo(center);
            if (r > 8.5f) continue;
            Color color = hover ? new Color("#ffcea0") : Orange;
            color.A = Mathf.Clamp(8.5f - r, 0, 1); image.SetPixel(x, y, color);
        }
        return ImageTexture.CreateFromImage(image);
    }
}
