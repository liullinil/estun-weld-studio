using Godot;
using System;
using System.Globalization;

namespace EstunStudio;

/// <summary>A tactile virtual teach pendant. Jogging always requires a held enabling device.</summary>
public partial class PendantPanel : Control
{
    public event Action<string>? Toast;
    public event Action? HomeViewRequested;

    private enum JogFrame { Joint, World, Tool }

    private static readonly Color Ink = new("#283337");
    private static readonly Color Muted = new("#718084");
    private static readonly Color Orange = new("#f49b4c");
    private static readonly Color Green = new("#73d6ad");
    private static readonly Color Screen = new("#e9eeeb");
    private static readonly Color ScreenLine = new("#cbd4cf");
    private static readonly string[] JointNames = { "J1", "J2", "J3", "J4", "J5", "J6" };
    private static readonly string[] JointCaptions = { "BASE", "SHOULDER", "ELBOW", "WRIST 1", "WRIST 2", "FLANGE" };
    private static readonly string[] TcpNames = { "X", "Y", "Z", "A", "B", "C" };
    private static readonly string[] TcpCaptions = { "POSITION", "POSITION", "POSITION", "ROLL", "PITCH", "YAW" };

    private RobotController? _controller;
    private Font? _font;
    private Font? _semibold;
    private readonly Label[] _axisNames = new Label[6];
    private readonly Label[] _axisCaptions = new Label[6];
    private readonly Label[] _axisValues = new Label[6];
    private readonly Label[] _axisUnits = new Label[6];
    private readonly Button[] _rowSelectors = new Button[6];
    private readonly Button[] _tabs = new Button[3];
    private Label _frameDescription = null!;
    private Label _speedValue = null!;
    private Label _statusLabel = null!;
    private Label _waypointLabel = null!;
    private Label _drivesCaption = null!;
    private Label _enableCaption = null!;
    private Label _runCaption = null!;
    private Label _enableHint = null!;
    private Button _drivesButton = null!;
    private Button _enableButton = null!;
    private Button _runButton = null!;
    private Button _emergencyButton = null!;
    private HSlider _speedSlider = null!;
    private JogFrame _frame;
    private int _selectedAxis;
    private int _pointerJogAxis = -1;
    private int _keyboardJogDirection;
    private bool _keyboardEnable;
    private bool _pointerEnable;
    private bool _emergencyHovered;
    private bool _lastEstop;
    private bool _lastDrives;
    private bool _lastHold;
    private float _readoutTimer;
    private double _pulseTime;

    public void Build(RobotController controller)
    {
        _controller = controller;
        Name = "TeachPendant";
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.TopRight);
        AnchorBottom = 1;
        OffsetLeft = -468;
        OffsetRight = -28;
        OffsetTop = 112;
        OffsetBottom = -70;
        CustomMinimumSize = new Vector2(440, 818);

        if (ResourceLoader.Exists("res://Assets/Fonts/Inter-Regular.ttf"))
            _font = GD.Load<Font>("res://Assets/Fonts/Inter-Regular.ttf");
        if (ResourceLoader.Exists("res://Assets/Fonts/Inter-SemiBold.ttf"))
            _semibold = GD.Load<Font>("res://Assets/Fonts/Inter-SemiBold.ttf");
        _font ??= ThemeDB.FallbackFont;
        _semibold ??= _font;
        _semibold = new FontVariation
        {
            BaseFont = _semibold,
            VariationOpentype = new Godot.Collections.Dictionary { ["wght"] = 650 },
        };
        AddThemeFontOverride("font", _font);

        BuildEnclosureHeader();
        BuildTouchscreen();
        BuildHardwareKeys();
        SelectFrame(JogFrame.Joint);
        RefreshState();
        QueueRedraw();
    }

    private void BuildEnclosureHeader()
    {
        LabelAt(this, "ESTUN", 26, 22, 215, 36, 29, new Color("#f3f5f1"), true);
        LabelAt(this, "ROBOTICS  /  TEACH PENDANT", 27, 63, 251, 16, 9, new Color("#8b9797"), true);
        LabelAt(this, "S20-180 PRO", 27, 81, 240, 14, 9, new Color("#b9c1bd"));

        _emergencyButton = new Button
        {
            Name = "EmergencyStop",
            Position = new Vector2(328, 13),
            Size = new Vector2(88, 88),
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "Emergency stop immediately stops motion and disables servo drives.\nEscape triggers emergency stop. Press RESET to clear the latch, then enable DRIVES.",
        };
        foreach (string state in new[] { "normal", "hover", "pressed", "focus", "disabled" })
            _emergencyButton.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        AddChild(_emergencyButton);
        _emergencyButton.Pressed += () =>
        {
            EmergencyStop();
            Toast?.Invoke("EMERGENCY STOP · Motion stopped. Press RESET, then enable DRIVES.");
        };
        _emergencyButton.MouseEntered += () => { _emergencyHovered = true; QueueRedraw(); };
        _emergencyButton.MouseExited += () => { _emergencyHovered = false; QueueRedraw(); };
        LabelAt(_emergencyButton, "STOP", 13, 23, 62, 24, 12, new Color("#ffeae5"), true, HorizontalAlignment.Center);
        LabelAt(this, "EMERGENCY STOP", 323, 91, 98, 12, 7, new Color("#aeb8b3"), true, HorizontalAlignment.Center);
    }

    private void BuildTouchscreen()
    {
        LabelAt(this, "JOG CONTROL", 34, 124, 240, 25, 20, Ink, true);
        Panel badge = new()
        {
            Position = new Vector2(300, 122),
            Size = new Vector2(106, 27),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        badge.AddThemeStyleboxOverride("panel", Flat(new Color("#dbe3dd"), 4, new Color("#c4cec7"), 1));
        AddChild(badge);
        LabelAt(badge, "T1  ·  MANUAL", 0, 0, 106, 27, 10, new Color("#53635c"), true, HorizontalAlignment.Center);
        LabelAt(this, "VIRTUAL CONTROLLER", 47, 158, 190, 15, 9, Muted, true);
        LabelAt(this, "6 AXES  /  SIMULATION", 249, 158, 155, 15, 9, Muted, false, HorizontalAlignment.Right);

        string[] tabs = { "JOINT", "WORLD", "TOOL" };
        string[] tooltips =
        {
            "Move individual joints J1–J6. Hold Space and press − or +.\nUse Up / Down to select an axis and Left / Right to jog.",
            "Move TCP along world coordinate axes: X, Y, Z in millimeters; A, B, C in degrees.\nY is vertical. Rotation readouts use YXZ Euler angles in the base coordinate system.",
            "Move TCP relative to the current tool orientation.\nTCP readouts remain in the robot base coordinate system. Rotation uses YXZ Euler angles.",
        };
        for (int i = 0; i < 3; i++)
        {
            int tab = i;
            _tabs[i] = MakeButton(this, tabs[i], 34 + i * 124, 186, 120, 35, false);
            _tabs[i].TooltipText = tooltips[i];
            _tabs[i].Pressed += () => SelectFrame((JogFrame)tab);
        }

        _frameDescription = LabelAt(this, "", 35, 232, 370, 17, 10, Muted, true);
        for (int i = 0; i < 6; i++)
        {
            int axis = i;
            float y = 258 + i * 44;
            _rowSelectors[i] = MakeButton(this, "", 28, y, 382, 41, false);
            _rowSelectors[i].AddThemeStyleboxOverride("normal", Flat(new Color("#f1f4f0"), 4));
            _rowSelectors[i].AddThemeStyleboxOverride("hover", Flat(new Color("#e2e9e3"), 4));
            _rowSelectors[i].AddThemeStyleboxOverride("pressed", Flat(new Color("#d7e3dc"), 4));
            _rowSelectors[i].TooltipText = "Select this axis. Hold Space and press − / +, or use Left / Right arrow keys.";
            _rowSelectors[i].Pressed += () => SelectAxis(axis);

            _axisNames[i] = LabelAt(this, JointNames[i], 40, y + 5, 41, 29, 17, Ink, true);
            _axisCaptions[i] = LabelAt(this, JointCaptions[i], 83, y + 9, 82, 23, 9, Muted, true);
            _axisValues[i] = LabelAt(this, "0.00", 147, y + 5, 96, 29, 17, Ink, false, HorizontalAlignment.Right);
            _axisUnits[i] = LabelAt(this, "°", 246, y + 7, 29, 25, 12, Muted);
            Button minus = MakeButton(this, "−", 283, y + 3, 54, 35, false);
            Button plus = MakeButton(this, "+", 343, y + 3, 54, 35, false);
            minus.AddThemeFontSizeOverride("font_size", 23);
            plus.AddThemeFontSizeOverride("font_size", 22);
            StyleJogButton(minus);
            StyleJogButton(plus);
            BindJog(minus, axis, -1);
            BindJog(plus, axis, 1);
        }

        LabelAt(this, "SPEED OVERRIDE", 35, 539, 230, 17, 10, Muted, true);
        _speedValue = LabelAt(this, "25", 325, 546, 48, 38, 29, Ink, true, HorizontalAlignment.Right);
        LabelAt(this, "%", 379, 557, 20, 22, 12, Muted, true);
        _speedSlider = new HSlider
        {
            Name = "SpeedOverride",
            Position = new Vector2(36, 562),
            Size = new Vector2(266, 27),
            MinValue = 1,
            MaxValue = 100,
            Step = 1,
            Value = _controller!.SpeedOverride * 100,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = "Adjust manual jog and program playback speed from 1 to 100 percent.",
        };
        StyleBoxFlat track = Flat(new Color("#c6d0c9"), 3);
        track.ContentMarginTop = 3;
        track.ContentMarginBottom = 3;
        StyleBoxFlat fill = Flat(new Color("#3f7467"), 3);
        fill.ContentMarginTop = 3;
        fill.ContentMarginBottom = 3;
        _speedSlider.AddThemeStyleboxOverride("slider", track);
        _speedSlider.AddThemeStyleboxOverride("grabber_area", fill);
        _speedSlider.AddThemeStyleboxOverride("grabber_area_highlight", fill);
        _speedSlider.AddThemeIconOverride("grabber", MakeSliderKnob(false));
        _speedSlider.AddThemeIconOverride("grabber_highlight", MakeSliderKnob(true));
        _speedSlider.AddThemeIconOverride("grabber_disabled", MakeSliderKnob(false));
        AddChild(_speedSlider);
        _speedSlider.ValueChanged += value => _controller!.SpeedOverride = (float)value / 100;
        LabelAt(this, "1", 35, 588, 20, 10, 7, Muted);
        LabelAt(this, "100", 282, 588, 24, 10, 7, Muted, false, HorizontalAlignment.Right);

        _statusLabel = LabelAt(this, "DRIVES OFF", 47, 609, 221, 17, 10, Muted, true);
        _waypointLabel = LabelAt(this, "00 POINTS", 285, 609, 118, 17, 10, Muted, true, HorizontalAlignment.Right);
    }

    private void BuildHardwareKeys()
    {
        _drivesButton = MakeButton(this, "", 22, 650, 168, 54, true);
        _drivesButton.TooltipText = "Turn servo drives on or off. Hold Space or HOLD TO ENABLE to permit motion.\nAfter an emergency stop, press RESET first.";
        _drivesButton.Pressed += () =>
        {
            _controller!.SetDrives(!_controller.DrivesEnabled);
            Toast?.Invoke(_controller.EmergencyStopped
                ? "Emergency stop is latched. Press RESET before enabling drives."
                : _controller.DrivesEnabled ? "Drives enabled · Hold SPACE, then jog an axis." : "Drives off · Robot motion stopped.");
        };
        AddIcon(_drivesButton, "power", 13, 14, 26, 26, new Color("#a5b4b0"));
        _drivesCaption = LabelAt(_drivesButton, "DRIVES OFF", 47, 9, 114, 22, 11, new Color("#d8dfda"), true);
        LabelAt(_drivesButton, "SERVO POWER", 48, 31, 113, 14, 7, new Color("#748580"), true);

        _enableButton = MakeButton(this, "", 201, 650, 217, 54, true);
        _enableButton.TooltipText = "Motion is permitted only while this enabling device is held.\nHold Space and press an on-screen − / + jog key.\nOr hold this button with the mouse and use Left / Right to jog the selected axis.";
        _enableButton.AddThemeStyleboxOverride("normal", Flat(new Color("#283f37"), 7, new Color("#4f7969"), 1));
        _enableButton.AddThemeStyleboxOverride("hover", Flat(new Color("#335346"), 7, new Color("#6c9c88"), 1));
        _enableButton.AddThemeStyleboxOverride("pressed", Flat(new Color("#486f5a"), 7, Green, 1));
        AddIcon(_enableButton, "enable", 13, 15, 26, 24, new Color("#a0c6ac"));
        _enableCaption = LabelAt(_enableButton, "HOLD TO ENABLE", 49, 9, 163, 22, 11, new Color("#d5e6d5"), true);
        LabelAt(_enableButton, "SPACE  /  DEADMAN", 49, 31, 157, 14, 7, new Color("#91ad95"), true);
        _enableButton.ButtonDown += () => { _pointerEnable = true; ApplyEnable(); };
        _enableButton.ButtonUp += () => { _pointerEnable = false; ApplyEnable(); };
        _enableButton.MouseExited += () =>
        {
            if (!_pointerEnable) return;
            _pointerEnable = false;
            ApplyEnable();
        };

        string[] titles = { "HOME", "RECORD", "RUN", "RESET" };
        string[] icons = { "home", "record", "play", "reset" };
        string[] hints =
        {
            "Move smoothly to the home pose. Enable DRIVES and hold Space until the movement is complete.",
            "Record the current robot pose as a program point. Points are saved automatically.\nRight-click to save the current program again.",
            "Play recorded points in order. Enable DRIVES and keep Space held.\nPress again to stop playback.",
            "Clear the emergency stop latch. Servo drives remain disabled.\nRight-click to clear all recorded program points.",
        };
        for (int i = 0; i < 4; i++)
        {
            Button button = MakeButton(this, "", 22 + i * 102, 728, 90, 55, true);
            button.TooltipText = hints[i];
            button.AddThemeStyleboxOverride("normal", Flat(new Color("#323a3a"), 6, new Color("#505b58"), 1));
            button.AddThemeStyleboxOverride("hover", Flat(new Color("#414c46"), 6, new Color("#788579"), 1));
            button.AddThemeStyleboxOverride("pressed", Flat(new Color("#212926"), 6, Orange, 1));
            AddIcon(button, icons[i], 36, 7, 20, 21, i == 1 ? Orange : new Color("#becbc1"));
            Label caption = LabelAt(button, titles[i], 0, 32, 90, 17, 9, new Color("#d1d9cf"), true, HorizontalAlignment.Center);
            if (i == 0) button.Pressed += () =>
            {
                if (!RequireMotionPermission()) return;
                _controller!.GoHome();
                HomeViewRequested?.Invoke();
                Toast?.Invoke("Returning to HOME · Keep SPACE held.");
            };
            if (i == 1)
            {
                button.Pressed += () =>
                {
                    if (!_controller!.RecordWaypoint())
                    {
                        Toast?.Invoke(_controller.Status);
                        return;
                    }
                    bool saved = _controller.SaveWaypoints();
                    Toast?.Invoke(saved ? $"Point P{_controller.Waypoints.Count:000} recorded and saved." : _controller.Status);
                };
                button.GuiInput += input =>
                {
                    if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                    {
                        bool saved = _controller!.SaveWaypoints();
                        Toast?.Invoke(saved ? "Robot program saved to the application data folder." : _controller.Status);
                    }
                };
            }
            if (i == 2)
            {
                _runButton = button;
                _runCaption = caption;
                button.Pressed += () =>
                {
                    if (_controller!.IsPlaying || _controller.MotionActive)
                    {
                        _controller.StopMotion();
                        Toast?.Invoke("Motion stopped.");
                        return;
                    }
                    if (!RequireMotionPermission()) return;
                    if (_controller.Waypoints.Count < 2)
                    {
                        Toast?.Invoke("Record at least two points to build a motion program.");
                        return;
                    }
                    _controller.PlayWaypoints();
                    Toast?.Invoke("Program running · Keep SPACE held. Release to stop.");
                };
            }
            if (i == 3)
            {
                button.Pressed += () =>
                {
                    _keyboardEnable = false;
                    _pointerEnable = false;
                    _pointerJogAxis = -1;
                    _keyboardJogDirection = 0;
                    _controller!.StopMotion();
                    _controller.ResetEmergencyStop();
                    Toast?.Invoke("Controller reset · Enable DRIVES to continue.");
                };
                button.GuiInput += input =>
                {
                    if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true })
                    {
                        _controller!.ClearWaypoints();
                        _controller.SaveWaypoints();
                        Toast?.Invoke("Motion program cleared.");
                    }
                };
            }
        }
        _enableHint = LabelAt(this, "HOLD SPACE + JOG  ·  ESC EMERGENCY STOP", 25, 795, 390, 12, 8, new Color("#738079"), true, HorizontalAlignment.Center);
    }

    public override void _Input(InputEvent input)
    {
        if (_controller is null) return;
        if (input is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false })
        {
            StopPointerJog();
            if (_pointerEnable)
            {
                _pointerEnable = false;
                ApplyEnable();
            }
            return;
        }
        if (input is not InputEventKey key || key.Echo) return;
        Key code = key.PhysicalKeycode != Key.None ? key.PhysicalKeycode : key.Keycode;
        // Input callbacks still run when an ancestor hides this control (cinema mode).
        // Keep releases and emergency stop live, but never arm invisible jog controls.
        if (!IsVisibleInTree() && key.Pressed && code != Key.Escape) return;
        if (code == Key.Space)
        {
            _keyboardEnable = key.Pressed;
            ApplyEnable();
            GetViewport().SetInputAsHandled();
        }
        else if (code == Key.Escape && key.Pressed)
        {
            EmergencyStop();
            Toast?.Invoke("EMERGENCY STOP · Press RESET, then enable DRIVES.");
            GetViewport().SetInputAsHandled();
        }
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
        _pulseTime += delta;

        // Window focus changes and releases outside any button must never leave a jog latched.
        if ((_pointerJogAxis >= 0 || _pointerEnable) && !Input.IsMouseButtonPressed(MouseButton.Left))
        {
            StopPointerJog();
            _pointerEnable = false;
            ApplyEnable();
        }
        if (_keyboardEnable && !Input.IsPhysicalKeyPressed(Key.Space))
        {
            _keyboardEnable = false;
            ApplyEnable();
        }
        if (_lastEstop != _controller.EmergencyStopped || _lastDrives != _controller.DrivesEnabled || _lastHold != _controller.HoldToRun)
        {
            RefreshState();
            QueueRedraw();
        }
        _readoutTimer += (float)delta;
        if (_readoutTimer < 1f / 20f) return;
        _readoutTimer = 0;
        RefreshReadouts();
        if (_controller.EmergencyStopped) QueueRedraw();
    }

    public override void _ExitTree()
    {
        if (_controller is null || !GodotObject.IsInstanceValid(_controller)) return;
        _controller.HoldToRun = false;
        _controller.StopMotion();
    }

    public override void _Notification(int what)
    {
        if (what != NotificationApplicationFocusOut || _controller is null) return;
        _keyboardEnable = false;
        _pointerEnable = false;
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _controller.HoldToRun = false;
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
            _tabs[i].AddThemeStyleboxOverride("normal", Flat(active ? new Color("#344d46") : new Color("#dee5df"), 5, active ? new Color("#344d46") : new Color("#cbd5cc"), 1));
            _tabs[i].AddThemeStyleboxOverride("hover", Flat(active ? new Color("#3b5b4e") : new Color("#d0dcd2"), 5));
            _tabs[i].AddThemeColorOverride("font_color", active ? new Color("#f2f5ee") : Muted);
            _tabs[i].AddThemeColorOverride("font_hover_color", active ? new Color("#ffffff") : Ink);
        }
        _frameDescription.Text = frame switch
        {
            JogFrame.Joint => "AXIS POSITION  /  DEGREES",
            JogFrame.World => "TCP IN BASE  /  mm · °  /  WORLD JOG",
            _ => "TCP IN BASE  /  mm · °  /  TOOL-RELATIVE JOG",
        };
        for (int i = 0; i < 6; i++)
        {
            _axisNames[i].Text = frame == JogFrame.Joint ? JointNames[i] : TcpNames[i];
            _axisCaptions[i].Text = frame == JogFrame.Joint ? JointCaptions[i] : TcpCaptions[i];
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
            _rowSelectors[i].AddThemeStyleboxOverride("normal", Flat(active ? new Color("#dbe7dd") : new Color("#f1f4f0"), 4, active ? new Color("#b5ccbb") : new Color("#e1e7e0"), 1));
            _axisNames[i].AddThemeColorOverride("font_color", active ? new Color("#32634f") : Ink);
        }
    }

    private void BindJog(Button button, int axis, int direction)
    {
        button.TooltipText = $"Axis {axis + 1}: {(direction > 0 ? "positive" : "negative")} direction.\nEnable DRIVES, then hold Space and this button. Release either to stop motion.";
        button.ButtonDown += () => BeginJog(axis, direction, true);
        button.ButtonUp += StopPointerJog;
        button.MouseExited += () =>
        {
            if (_pointerJogAxis == axis) StopPointerJog();
        };
    }

    private void BeginJog(int axis, int direction, bool pointer)
    {
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
            Toast?.Invoke("Enable DRIVES, then hold SPACE while jogging.");
            return false;
        }
        if (!_controller.HoldToRun)
        {
            Toast?.Invoke("Hold SPACE + press − / + to move. Release SPACE to stop.");
            return false;
        }
        return true;
    }

    private void ApplyEnable()
    {
        if (_controller is null) return;
        _controller.HoldToRun = IsVisibleInTree() && !_controller.EmergencyStopped && (_keyboardEnable || _pointerEnable);
        if (!_controller.HoldToRun)
        {
            _controller.StopMotion();
            _pointerJogAxis = -1;
            _keyboardJogDirection = 0;
        }
        RefreshState();
        QueueRedraw();
    }

    private void EmergencyStop()
    {
        _keyboardEnable = false;
        _pointerEnable = false;
        _pointerJogAxis = -1;
        _keyboardJogDirection = 0;
        _controller!.HoldToRun = false;
        _controller.EmergencyStop();
        RefreshState();
        QueueRedraw();
    }

    private void RefreshState()
    {
        if (_controller is null || _drivesCaption is null) return;
        _lastEstop = _controller.EmergencyStopped;
        _lastDrives = _controller.DrivesEnabled;
        _lastHold = _controller.HoldToRun;
        _drivesCaption.Text = _lastDrives ? "DRIVES ON" : "DRIVES OFF";
        _drivesCaption.AddThemeColorOverride("font_color", _lastDrives ? Green : new Color("#d8dfda"));
        _drivesButton.AddThemeStyleboxOverride("normal", Flat(_lastDrives ? new Color("#2f4840") : new Color("#30393a"), 7, _lastDrives ? new Color("#6ea58a") : new Color("#566461"), 1));
        _enableCaption.Text = _lastHold ? "ENABLE ACTIVE" : "HOLD TO ENABLE";
        _enableButton.AddThemeStyleboxOverride("normal", Flat(_lastHold ? new Color("#486f5a") : new Color("#283f37"), 7, _lastHold ? Green : new Color("#4f7969"), 1));
        _enableHint.Text = _lastEstop ? "EMERGENCY STOP ACTIVE  ·  PRESS RESET" : _lastHold ? "ENABLING DEVICE HELD  ·  RELEASE TO STOP" : "HOLD SPACE + JOG  ·  ESC EMERGENCY STOP";
        _enableHint.AddThemeColorOverride("font_color", _lastEstop ? new Color("#f09b87") : _lastHold ? new Color("#a0c7a5") : new Color("#738079"));
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
        _speedValue.Text = Math.Round(_controller.SpeedOverride * 100).ToString(CultureInfo.InvariantCulture);
        string status = _controller.EmergencyStopped ? "EMERGENCY STOP" : !_controller.DrivesEnabled ? "DRIVES OFF · STANDBY" : !_controller.LastIkSucceeded ? "TCP TARGET UNREACHABLE" : _controller.Status.Contains("limit", StringComparison.OrdinalIgnoreCase) ? "JOINT LIMIT REACHED" : _controller.IsPlaying ? "PROGRAM RUNNING" : _controller.MotionActive ? "MOTION ACTIVE" : _controller.HoldToRun ? "READY TO MOVE" : "HOLD SPACE TO JOG";
        _statusLabel.Text = status;
        _statusLabel.AddThemeColorOverride("font_color", _controller.EmergencyStopped ? new Color("#a53d32") : !_controller.LastIkSucceeded ? new Color("#a26828") : _controller.DrivesEnabled ? new Color("#3b6d56") : Muted);
        _waypointLabel.Text = $"{_controller.Waypoints.Count:00} POINTS";
        _runCaption.Text = _controller.IsPlaying || _controller.MotionActive ? "STOP" : "RUN";
    }

    private static string Format(float value, int decimals)
    {
        if (Math.Abs(value) < (decimals == 2 ? .005f : .05f)) value = 0;
        return value.ToString(decimals == 2 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
    }

    public override void _Draw()
    {
        float h = Math.Max(818, Size.Y);
        StyleBoxFlat shadow = Flat(new Color("#151b1c"), 23, new Color("#070d0e"), 1);
        shadow.ShadowColor = new Color(0, 0, 0, .40f);
        shadow.ShadowSize = 19;
        shadow.ShadowOffset = new Vector2(0, 10);
        DrawStyleBox(shadow, new Rect2(0, 0, 440, h));
        DrawStyleBox(Flat(new Color("#202727"), 20, new Color("#62615a"), 1), new Rect2(5, 5, 430, h - 10));
        DrawStyleBox(Flat(new Color("#222b2b"), 16, new Color("#363f3c"), 1), new Rect2(11, 11, 418, h - 22));
        DrawLine(new Vector2(29, 8), new Vector2(302, 8), new Color("#c07c40"), 2, true);
        DrawLine(new Vector2(438, 128), new Vector2(438, h - 88), new Color("#956b42"), 2, true);
        DrawLine(new Vector2(2, 128), new Vector2(2, h - 88), new Color("#956b42"), 2, true);

        // Machined housing grooves and recessed fasteners are part of the enclosure.
        for (int i = 0; i < 9; i++)
        {
            float y = 155 + i * 47;
            DrawLine(new Vector2(6, y), new Vector2(10, y + 18), new Color("#0f1717"), 2, true);
            DrawLine(new Vector2(430, y), new Vector2(434, y + 18), new Color("#0f1717"), 2, true);
        }
        DrawScrew(new Vector2(17, 17));
        DrawScrew(new Vector2(423, 17));
        DrawScrew(new Vector2(17, h - 17));
        DrawScrew(new Vector2(423, h - 17));

        DrawStyleBox(Flat(new Color("#111918"), 12, new Color("#59645e"), 1), new Rect2(17, 110, 406, 531));
        DrawStyleBox(Flat(Screen, 8, new Color("#bbc7be"), 1), new Rect2(22, 115, 396, 521));
        DrawLine(new Vector2(34, 178), new Vector2(405, 178), ScreenLine, 1, true);
        DrawLine(new Vector2(34, 530), new Vector2(405, 530), ScreenLine, 1, true);
        DrawLine(new Vector2(34, 603), new Vector2(405, 603), ScreenLine, 1, true);
        DrawCircle(new Vector2(38, 165), 3, new Color("#50876a"), true, -1, true);
        Color statusColor = _controller?.EmergencyStopped == true ? new Color("#b45540") : _controller?.DrivesEnabled == true ? new Color("#51866a") : new Color("#a4aaa3");
        DrawCircle(new Vector2(38, 617), 3, statusColor, true, -1, true);

        DrawLine(new Vector2(25, 717), new Vector2(415, 717), new Color("#3b4640"), 1, true);
        DrawLine(new Vector2(25, 719), new Vector2(415, 719), new Color("#111d18"), 1, true);
        DrawEmergencyButton();
    }

    private void DrawEmergencyButton()
    {
        Vector2 center = new(372, 54);
        bool stopped = _controller?.EmergencyStopped == true;
        if (stopped)
        {
            float alpha = .12f + (float)(Math.Sin(_pulseTime * 4) + 1) * .08f;
            DrawCircle(center, 39, new Color(1, .25f, .13f, alpha), true, -1, true);
        }
        DrawCircle(center + new Vector2(0, 3), 34, new Color("#0c1212"), true, -1, true);
        DrawCircle(center, 33, new Color(_emergencyHovered ? "#ecc870" : "#c8ab5e"), true, -1, true);
        DrawArc(center, 31, -.8f, 3.8f, 48, new Color("#ead08b"), 1, true);
        DrawCircle(center, 27, new Color("#342822"), true, -1, true);
        DrawCircle(center + new Vector2(0, stopped ? 2 : 1), 24, new Color("#741d18"), true, -1, true);
        DrawCircle(center + new Vector2(0, stopped ? 2 : -2), 22, new Color(_emergencyHovered ? "#e65c47" : "#cf4b38"), true, -1, true);
        DrawArc(center + new Vector2(0, -2), 19, 3.55f, 5.85f, 28, new Color("#f2856b"), 2, true);
        DrawArc(center + new Vector2(0, 0), 22, .15f, 2.65f, 24, new Color("#9c3025"), 2, true);
    }

    private void DrawScrew(Vector2 at)
    {
        DrawCircle(at + new Vector2(0, 1), 5.5f, new Color("#101916"), true, -1, true);
        DrawCircle(at, 4.2f, new Color("#535c54"), true, -1, true);
        DrawArc(at, 4, 3.3f, 6, 16, new Color("#879084"), 1, true);
        DrawLine(at + new Vector2(-1.7f, -1.7f), at + new Vector2(1.7f, 1.7f), new Color("#19231d"), 1.5f, true);
    }

    private Button MakeButton(Control parent, string text, float x, float y, float width, float height, bool dark)
    {
        Button button = new()
        {
            Text = text,
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
        };
        button.AddThemeFontOverride("font", _semibold!);
        button.AddThemeFontSizeOverride("font_size", 12);
        button.AddThemeColorOverride("font_color", dark ? new Color("#d8dfd8") : Ink);
        button.AddThemeColorOverride("font_hover_color", dark ? Colors.White : Ink);
        button.AddThemeColorOverride("font_pressed_color", dark ? Colors.White : Ink);
        button.AddThemeStyleboxOverride("normal", Flat(dark ? new Color("#30393a") : new Color("#dbe3dc"), 6, dark ? new Color("#566461") : new Color("#c0cdc1"), 1));
        button.AddThemeStyleboxOverride("hover", Flat(dark ? new Color("#3d4e46") : new Color("#ceded1"), 6, dark ? new Color("#859486") : new Color("#a7c4ad"), 1));
        button.AddThemeStyleboxOverride("pressed", Flat(dark ? new Color("#1f2f26") : new Color("#bbd0bf"), 6, new Color("#7eac8c"), 1));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        parent.AddChild(button);
        return button;
    }

    private void StyleJogButton(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Flat(new Color("#e4eae2"), 5, new Color("#bdcabe"), 1));
        button.AddThemeStyleboxOverride("hover", Flat(new Color("#d2e0d1"), 5, new Color("#7d9d83"), 1));
        button.AddThemeStyleboxOverride("pressed", Flat(new Color("#426e55"), 5, new Color("#35593f"), 1));
        button.AddThemeColorOverride("font_pressed_color", new Color("#ffffff"));
        button.AddThemeColorOverride("font_color", new Color("#526757"));
    }

    private Label LabelAt(Control parent, string text, float x, float y, float width, float height, int size, Color color, bool bold = false, HorizontalAlignment alignment = HorizontalAlignment.Left)
    {
        Label label = new()
        {
            ClipText = true,
            Text = text,
            Position = new Vector2(x, y),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = alignment,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontOverride("font", (bold ? _semibold : _font)!);
        label.AddThemeFontSizeOverride("font_size", Math.Max(9, size));
        label.AddThemeColorOverride("font_color", color);
        // Apply the requested rectangle after the final font metrics, preventing
        // Godot's initial default-font minimum width from widening small captions.
        label.Size = new Vector2(width, height);
        parent.AddChild(label);
        return label;
    }

    private static StyleBoxFlat Flat(Color color, int radius, Color? border = null, int borderWidth = 0)
    {
        return new StyleBoxFlat
        {
            BgColor = color,
            BorderColor = border ?? color,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            CornerDetail = 12,
            AntiAliasing = true,
        };
    }

    private static Texture2D MakeSliderKnob(bool hover)
    {
        const int size = 22;
        Image image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
        Vector2 center = new(10.5f, 10.5f);
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float r = new Vector2(x, y).DistanceTo(center);
            if (r > 10.5f) continue;
            Color color = r > 8.5f ? new Color("#668a74") : new Color(hover ? "#f4f8f0" : "#e8f0e4");
            color.A = Mathf.Clamp(10.5f - r, 0, 1);
            image.SetPixel(x, y, color);
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static void AddIcon(Control parent, string kind, float x, float y, float width, float height, Color color)
    {
        Control icon = new()
        {
            Position = new Vector2(x, y),
            Size = new Vector2(width, height),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        parent.AddChild(icon);
        icon.Draw += () =>
        {
            Vector2 center = icon.Size * .5f;
            float s = Math.Min(icon.Size.X, icon.Size.Y);
            void Line(float ax, float ay, float bx, float by, float weight = 1.7f) => icon.DrawLine(new Vector2(ax, ay) * s, new Vector2(bx, by) * s, color, weight, true);
            switch (kind)
            {
                case "power":
                    icon.DrawArc(center, s * .31f, -.95f, 4.10f, 30, color, 1.8f, true);
                    Line(.5f, .10f, .5f, .49f, 2);
                    break;
                case "enable":
                    Line(.25f, .68f, .25f, .30f);
                    Line(.40f, .63f, .40f, .17f);
                    Line(.55f, .62f, .55f, .20f);
                    Line(.70f, .63f, .70f, .32f);
                    icon.DrawArc(new Vector2(.46f, .64f) * s, s * .25f, -.15f, 3.12f, 18, color, 1.8f, true);
                    Line(.23f, .68f, .09f, .52f);
                    break;
                case "home":
                    Line(.10f, .47f, .50f, .12f);
                    Line(.50f, .12f, .90f, .47f);
                    Line(.24f, .40f, .24f, .87f);
                    Line(.24f, .87f, .76f, .87f);
                    Line(.76f, .87f, .76f, .40f);
                    Line(.50f, .85f, .50f, .60f);
                    break;
                case "record":
                    icon.DrawCircle(center, s * .29f, color, true, -1, true);
                    icon.DrawArc(center, s * .43f, 0, Mathf.Tau, 32, new Color(color, .3f), 1, true);
                    break;
                case "play":
                    icon.DrawColoredPolygon(new[] { new Vector2(.30f, .14f) * s, new Vector2(.86f, .50f) * s, new Vector2(.30f, .86f) * s }, color);
                    break;
                case "reset":
                    icon.DrawArc(center, s * .33f, -.7f, 4.1f, 28, color, 1.8f, true);
                    Line(.15f, .18f, .16f, .46f);
                    Line(.16f, .46f, .44f, .36f);
                    break;
            }
        };
    }
}
