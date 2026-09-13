using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EstunStudio;

/// <summary>
/// Offline six-axis robot simulator. Distances are metres, angles in the public API
/// are degrees, and the robot base uses Godot's right-handed Y-up coordinates.
/// This class has no connection to industrial robot hardware.
/// </summary>
public partial class RobotController : Node
{
    // Exact ENCY S20-180 Pro mechanism limits and home. Nominal joint speed
    // limits are from ESTUN's S-Pro specification; jog override scales them.
    public static readonly float[] JointMinimum = { -360, -360, -160, -360, -360, -360 };
    public static readonly float[] JointMaximum = { 360, 360, 160, 360, 360, 360 };
    public static readonly float[] HomeAngles = { 0, 90, 0, 0, 90, 0 };
    public static readonly float[] CadPoseAngles = { 0, 90, -90, 0, 0, 0 };
    public static readonly Vector3[] JointAxes = { Vector3.Down, Vector3.Down, Vector3.Down, Vector3.Down, Vector3.Down, Vector3.Down };
    private static readonly float[] JointSpeed = { 110, 110, 150, 180, 180, 180 };

    private Node3D[] _joints = Array.Empty<Node3D>();
    private Transform3D[] _rest = Array.Empty<Transform3D>();
    private Transform3D _toolRest = Transform3D.Identity;
    private readonly float[] _angles = (float[])HomeAngles.Clone();
    private readonly float[] _jointVelocity = new float[6];
    private readonly List<RobotWaypoint> _waypoints = new();
    private bool _initialized;
    private bool _holdToRun;
    private float _speedOverride = .25f;
    private int _jogAxis = -1;
    private int _jogDirection;
    private bool _cartesianJog;
    private bool _toolFrame;
    private bool _goingHome;
    private bool _externalMotion;
    private string _externalCaption = "Welding simulation";
    private int _playIndex = -1;
    private float[]? _segmentStart;
    private float[]? _segmentTarget;
    private float _segmentProgress;
    private float _segmentDuration;
    private string _status = "Drives off · simulation ready";

    public event Action? PoseChanged;
    public event Action? StatusChanged;
    public event Action? WaypointsChanged;

    public float[] AnglesDegrees => (float[])_angles.Clone();
    public Transform3D TcpTransform => _initialized ? RobotKinematics.Forward(_angles, _rest, _toolRest) : Transform3D.Identity;
    public Vector3 TcpPosition => TcpTransform.Origin;
    public Vector3 TcpRotationDegrees => TcpTransform.Basis.GetEuler() * (180f / Mathf.Pi);
    public bool DrivesEnabled { get; private set; }
    public bool EmergencyStopped { get; private set; }
    public string Status => _status;
    public bool IsPlaying => _playIndex >= 0;
    public bool MotionActive => _jogDirection != 0 || _goingHome || IsPlaying || _externalMotion;
    public bool CanMove => _initialized && DrivesEnabled && !EmergencyStopped && HoldToRun;
    public bool LastIkSucceeded { get; private set; } = true;
    public bool IsPlannedMotion => _externalMotion;
    public IReadOnlyList<RobotWaypoint> Waypoints => _waypoints.AsReadOnly();
    public string WaypointFilePath => "user://estun_s20_180_pro_torch_waypoints.json";
    public string ActiveMotion => _externalMotion ? _externalCaption : IsPlaying ? $"Program · point {_playIndex + 1}/{_waypoints.Count}" : _goingHome ? "Moving to home" : _jogDirection != 0 ? (_cartesianJog ? "TCP jog" : $"Joint A{_jogAxis + 1} jog") : "Standstill";

    public bool HoldToRun
    {
        get => _holdToRun;
        set
        {
            if (_holdToRun == value) return;
            _holdToRun = value;
            if (!value)
            {
                ClearMotion();
                SetStatus(EmergencyStopped ? "Emergency stop latched" : DrivesEnabled ? "Hold enable to move" : "Drives off");
            }
            else SetStatus(EmergencyStopped ? "Reset emergency stop first" : DrivesEnabled ? "Enabled · ready to jog" : "Switch drives on");
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Normalized velocity override, 0.01–1.00.</summary>
    public float SpeedOverride
    {
        get => _speedOverride;
        set
        {
            if (!float.IsFinite(value)) return;
            _speedOverride = Mathf.Clamp(value, .01f, 1f);
            StatusChanged?.Invoke();
        }
    }

    public void Initialize(Node3D[] joints, Node3D tip)
    {
        if (joints.Length != 6 || joints.Any(j => j == null))
            throw new ArgumentException("Exactly six nested joint pivots are required.", nameof(joints));
        for (int i = 1; i < 6; ++i)
            if (joints[i].GetParent() != joints[i - 1])
                throw new ArgumentException("Joint pivots must form a direct parent-child chain.", nameof(joints));
        if (tip.GetParent() != joints[5])
            throw new ArgumentException("The tool centre point must be a direct child of joint six.", nameof(tip));
        _joints = joints;
        _rest = joints.Select(j => j.Transform).ToArray();
        _toolRest = tip.Transform;
        _initialized = true;
        ApplyPose();
        SetPhysicsProcess(true);
    }

    public bool SetDrives(bool enabled)
    {
        if (enabled && EmergencyStopped)
        {
            SetStatus("Reset emergency stop before enabling drives");
            return false;
        }
        DrivesEnabled = enabled;
        if (!enabled) ClearMotion();
        SetStatus(enabled ? HoldToRun ? "Enabled · ready to jog" : "Drives on · hold enable to move" : "Drives off");
        StatusChanged?.Invoke();
        return true;
    }

    public void EmergencyStop()
    {
        EmergencyStopped = true;
        DrivesEnabled = false;
        _holdToRun = false;
        ClearMotion();
        SetStatus("Emergency stop latched · reset required");
        StatusChanged?.Invoke();
    }

    public void ResetEmergencyStop()
    {
        EmergencyStopped = false;
        DrivesEnabled = false;
        _holdToRun = false;
        ClearMotion();
        SetStatus("Emergency stop reset · drives off");
        StatusChanged?.Invoke();
    }

    public bool SetJointJog(int axis, int direction) => BeginJog(axis, direction, false, false);
    public bool SetTcpJog(int axis, int direction, bool toolFrame) => BeginJog(axis, direction, true, toolFrame);

    private bool BeginJog(int axis, int direction, bool cartesian, bool toolFrame)
    {
        if (direction == 0) { StopJog(); return true; }
        if (axis < 0 || axis >= 6) { SetStatus("Invalid jog axis"); return false; }
        if (!RequireMotionPermission()) return false;
        ClearMotion();
        _jogAxis = axis;
        _jogDirection = Math.Sign(direction);
        _cartesianJog = cartesian;
        _toolFrame = toolFrame;
        LastIkSucceeded = true;
        SetStatus(cartesian ? $"Jogging TCP · {(toolFrame ? "TOOL" : "BASE")}" : $"Jogging A{axis + 1}");
        return true;
    }

    public void StopJog()
    {
        _jogAxis = -1;
        _jogDirection = 0;
        Array.Clear(_jointVelocity);
        if (!_goingHome && !IsPlaying)
            SetStatus(EmergencyStopped ? "Emergency stop latched" : !DrivesEnabled ? "Drives off" : HoldToRun ? "Enabled · standstill" : "Hold enable to move");
    }

    public void StopMotion()
    {
        ClearMotion();
        SetStatus(EmergencyStopped ? "Emergency stop latched" : "Motion stopped");
    }

    public bool GoHome()
    {
        if (!RequireMotionPermission()) return false;
        ClearMotion();
        _goingHome = true;
        StartSegment(HomeAngles);
        SetStatus("Moving to home · hold enable");
        return true;
    }

    /// <summary>Apply a pose produced by the application's validated offline planner. Interlocks remain active.</summary>
    public bool ApplyPlannedPose(float[] angles, string caption)
    {
        if (!CanMove || angles.Length != 6 || angles.Any(a => !float.IsFinite(a))) return false;
        for(int i=0;i<6;i++) if(angles[i]<JointMinimum[i] || angles[i]>JointMaximum[i]) return false;
        if(!_externalMotion) ClearMotion();
        _externalMotion=true;_externalCaption=caption;
        Array.Copy(angles,_angles,6);ApplyPose();return true;
    }

    public Transform3D ToolTransform => _toolRest;
    public Transform3D[] RestTransforms => (Transform3D[])_rest.Clone();

    public bool RecordWaypoint()
    {
        if (!_initialized) return false;
        if (_waypoints.Count >= 100)
        {
            SetStatus("Program is full · maximum 100 points");
            return false;
        }
        _waypoints.Add(new RobotWaypoint
        {
            Name = $"P{_waypoints.Count + 1:000}",
            AnglesDegrees = AnglesDegrees,
            SpeedOverride = SpeedOverride
        });
        SetStatus($"Recorded {_waypoints[^1].Name}");
        WaypointsChanged?.Invoke();
        return true;
    }

    public void ClearWaypoints()
    {
        if (IsPlaying) StopMotion();
        _waypoints.Clear();
        WaypointsChanged?.Invoke();
        SetStatus("Program cleared");
    }

    public bool PlayWaypoints()
    {
        if (!RequireMotionPermission()) return false;
        if (_waypoints.Count == 0) { SetStatus("Record a point before running the program"); return false; }
        ClearMotion();
        _playIndex = 0;
        StartSegment(_waypoints[0].AnglesDegrees);
        SetStatus($"Running {_waypoints[0].Name} · hold enable");
        return true;
    }

    public bool SaveWaypoints()
    {
        try
        {
            using var file = Godot.FileAccess.Open(WaypointFilePath, Godot.FileAccess.ModeFlags.Write);
            if (file == null) { SetStatus($"Save failed · {Godot.FileAccess.GetOpenError()}"); return false; }
            file.StoreString(JsonSerializer.Serialize(new WaypointDocument { Version = 1, RobotModel = "ESTUN S20-180 Pro / ENCY", Points = _waypoints.ToArray() }, new JsonSerializerOptions { WriteIndented = true }));
            if (file.GetError() != Error.Ok) { SetStatus($"Save failed · {file.GetError()}"); return false; }
            SetStatus($"Saved {_waypoints.Count} points to local storage");
            return true;
        }
        catch (Exception ex) { SetStatus($"Save failed · {ex.Message}"); return false; }
    }

    public bool LoadWaypoints()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(WaypointFilePath)) { SetStatus("No saved program yet"); return false; }
            using var file = Godot.FileAccess.Open(WaypointFilePath, Godot.FileAccess.ModeFlags.Read);
            if (file == null) { SetStatus("Cannot open saved program"); return false; }
            if (file.GetLength() > 1024 * 1024) { SetStatus("Saved program is too large"); return false; }
            var document = JsonSerializer.Deserialize<WaypointDocument>(file.GetAsText());
            if (document?.Version != 1 || document.RobotModel != "ESTUN S20-180 Pro / ENCY" || document.Points == null || document.Points.Length > 100 || document.Points.Any(p => !ValidWaypoint(p)))
            { SetStatus("Saved program is invalid · current program retained"); return false; }
            ClearMotion();
            _waypoints.Clear();
            _waypoints.AddRange(document.Points);
            WaypointsChanged?.Invoke();
            SetStatus($"Loaded {_waypoints.Count} points");
            return true;
        }
        catch (Exception ex) { SetStatus($"Load failed · {ex.Message}"); return false; }
    }

    public override void _Notification(int what)
    {
        // Focus loss must never leave a mouse/key jog running in the background.
        if (what == NotificationApplicationFocusOut) HoldToRun = false;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_initialized || !CanMove) return;
        float dt = (float)Math.Min(delta, .05);
        if (dt <= 0 || !float.IsFinite(dt)) return;
        if (_jogDirection != 0)
        {
            if (_cartesianJog) TickCartesianJog(dt);
            else TickJointJog(dt);
        }
        else if (_goingHome || IsPlaying) TickSegment(dt);
    }

    private void TickJointJog(float dt)
    {
        int axis = _jogAxis;
        float targetVelocity = JointSpeed[axis] * SpeedOverride * _jogDirection;
        _jointVelocity[axis] = Mathf.MoveToward(_jointVelocity[axis], targetVelocity, 160f * dt);
        float next = _angles[axis] + _jointVelocity[axis] * dt;
        float clamped = Mathf.Clamp(next, JointMinimum[axis], JointMaximum[axis]);
        _angles[axis] = clamped;
        ApplyPose();
        if (clamped != next)
        {
            _jogDirection = 0;
            Array.Clear(_jointVelocity);
            SetStatus($"A{axis + 1} joint limit reached");
        }
    }

    private void TickCartesianJog(float dt)
    {
        Transform3D current = TcpTransform;
        Transform3D target = current;
        Vector3 axis = _jogAxis % 3 == 0 ? Vector3.Right : _jogAxis % 3 == 1 ? Vector3.Up : Vector3.Back;
        float deltaPosition = 0;
        float deltaAngle = 0;
        if (_jogAxis < 3)
        {
            deltaPosition = .25f * SpeedOverride * dt;
            Vector3 direction = _toolFrame ? current.Basis * axis : axis;
            target.Origin += direction * deltaPosition * _jogDirection;
        }
        else
        {
            deltaAngle = Mathf.DegToRad(60f) * SpeedOverride * dt;
            Basis turn = new(axis, deltaAngle * _jogDirection);
            target.Basis = (_toolFrame ? current.Basis * turn : turn * current.Basis).Orthonormalized();
        }
        var solution = RobotKinematics.Solve(target, _angles, _rest, _toolRest,
            Math.Max(.000015f, deltaPosition * .12f), Math.Max(.00008f, deltaAngle * .12f));
        // Cap actual joint speed even when the Jacobian is poorly conditioned.
        if (solution.Success)
        {
            float factor = 1f;
            for (int i = 0; i < 6; i++)
            {
                float difference = Math.Abs(solution.AnglesDegrees[i] - _angles[i]);
                if (difference > .000001f) factor = Math.Min(factor, JointSpeed[i] * SpeedOverride * dt / difference);
            }
            if (factor > .015f)
            {
                for (int i = 0; i < 6; i++) _angles[i] = Mathf.Lerp(_angles[i], solution.AnglesDegrees[i], factor);
                LastIkSucceeded = true;
                ApplyPose();
                SetStatus(factor < .95f ? "TCP jog · reduced near singularity" : $"Jogging TCP · {(_toolFrame ? "TOOL" : "BASE")}");
                return;
            }
        }
        LastIkSucceeded = false;
        SetStatus("TCP limit / singularity · choose another direction");
    }

    private void StartSegment(float[] target)
    {
        _segmentStart = AnglesDegrees;
        _segmentTarget = (float[])target.Clone();
        _segmentProgress = 0;
        _segmentDuration = .3f;
        for (int i = 0; i < 6; ++i)
            _segmentDuration = Math.Max(_segmentDuration, 1.875f * Math.Abs(target[i] - _angles[i]) / JointSpeed[i]);
    }

    private void TickSegment(float dt)
    {
        if (_segmentStart == null || _segmentTarget == null) { ClearMotion(); return; }
        float waypointOverride = IsPlaying ? _waypoints[_playIndex].SpeedOverride : 1f;
        _segmentProgress = Math.Min(1f, _segmentProgress + dt * Math.Min(SpeedOverride, waypointOverride) / _segmentDuration);
        float t = _segmentProgress;
        // Quintic smoothstep gives zero velocity and acceleration at each endpoint.
        float smooth = t * t * t * (10f + t * (-15f + 6f * t));
        for (int i = 0; i < 6; ++i) _angles[i] = Mathf.Lerp(_segmentStart[i], _segmentTarget[i], smooth);
        ApplyPose();
        if (t < 1f) return;
        if (IsPlaying && ++_playIndex < _waypoints.Count)
        {
            StartSegment(_waypoints[_playIndex].AnglesDegrees);
            SetStatus($"Running {_waypoints[_playIndex].Name} · hold enable");
        }
        else
        {
            bool wasHome = _goingHome;
            ClearMotion();
            SetStatus(wasHome ? "Home position reached" : "Program complete");
        }
    }

    private bool RequireMotionPermission()
    {
        if (!_initialized) { SetStatus("Robot is initializing"); return false; }
        if (EmergencyStopped) { SetStatus("Emergency stop is latched"); return false; }
        if (!DrivesEnabled) { SetStatus("Enable drives before moving"); return false; }
        if (!HoldToRun) { SetStatus("Hold the enable switch to move"); return false; }
        return true;
    }

    private void ClearMotion()
    {
        _externalMotion = false;
        _jogAxis = -1;
        _jogDirection = 0;
        _goingHome = false;
        _playIndex = -1;
        _segmentStart = null;
        _segmentTarget = null;
        Array.Clear(_jointVelocity);
    }

    private void ApplyPose()
    {
        for (int i = 0; i < 6; ++i)
        {
            Transform3D t = _rest[i];
            t.Basis = t.Basis * new Basis(JointAxes[i], Mathf.DegToRad(_angles[i]));
            _joints[i].Transform = t;
        }
        PoseChanged?.Invoke();
    }

    private void SetStatus(string status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke();
    }

    private static bool ValidWaypoint(RobotWaypoint? point)
    {
        if (point == null || point.AnglesDegrees == null || point.AnglesDegrees.Length != 6 ||
            !float.IsFinite(point.SpeedOverride) || point.SpeedOverride < .01f || point.SpeedOverride > 1f ||
            string.IsNullOrWhiteSpace(point.Name) || point.Name.Length > 80) return false;
        for (int i = 0; i < 6; ++i)
            if (!float.IsFinite(point.AnglesDegrees[i]) || point.AnglesDegrees[i] < JointMinimum[i] || point.AnglesDegrees[i] > JointMaximum[i]) return false;
        return true;
    }

    public sealed class WaypointDocument
    {
        public int Version { get; set; }
        public string? RobotModel { get; set; }
        public RobotWaypoint[]? Points { get; set; }
    }
}

public sealed class RobotWaypoint
{
    public string Name { get; set; } = "Point";
    public float[] AnglesDegrees { get; set; } = new float[6];
    public float SpeedOverride { get; set; } = .25f;
}

/// <summary>Forward kinematics and bounded damped least-squares inverse kinematics.</summary>
public static class RobotKinematics
{
    private const float RotationWeight = .4f;
    public readonly record struct IkResult(bool Success, float[] AnglesDegrees, float PositionError, float OrientationError);

    // Imported from ENCY mechanismDescription.xml. ENCY local rotations use
    // Ry(q - DesignTimeValue), with DesignTimeValue [0,90,-90,0,0,0].
    // Converting ENCY's left-handed coordinates by swapping X/Z produces
    // right-handed Godot local -Y axes. These bases include the design offsets.
    // Source CAD -> Godot: (x,y,z) = (CAD y,CAD z,CAD x), millimetres to metres.
    public static Transform3D[] StandardRestTransforms() => new[]
    {
        new Transform3D(new Basis(Vector3.Left, Vector3.Up, Vector3.Forward), Vector3.Zero),
        new Transform3D(new Basis(Vector3.Down, Vector3.Forward, Vector3.Right), new Vector3(0, .216f, .255f)),
        new Transform3D(new Basis(Vector3.Back, Vector3.Up, Vector3.Left), new Vector3(0, 0, .850f)),
        new Transform3D(Basis.Identity, new Vector3(0, .203f, .765f)),
        new Transform3D(new Basis(Vector3.Right, Vector3.Back, Vector3.Down), new Vector3(0, -.167f, 0)),
        new Transform3D(new Basis(Vector3.Right, Vector3.Forward, Vector3.Up), new Vector3(0, .1625f, .1625f))
    };

    // The imported mechanism's end-effector connector is at the real J6 flange.
    public static Transform3D StandardToolTransform => Transform3D.Identity;

    public static Transform3D Forward(float[] degrees, Transform3D[] rest, Transform3D tool)
    {
        Transform3D result = Transform3D.Identity;
        for (int i = 0; i < 6; ++i)
        {
            Transform3D joint = rest[i];
            joint.Basis = joint.Basis * new Basis(RobotController.JointAxes[i], Mathf.DegToRad(degrees[i]));
            result *= joint;
        }
        return result * tool;
    }

    public static IkResult Solve(Transform3D target, float[] initialDegrees, Transform3D[] rest, Transform3D tool,
        float positionTolerance = .0002f, float orientationTolerance = .001f)
    {
        float[] angles = (float[])initialDegrees.Clone();
        if (angles.Length != 6 || rest.Length != 6 || !Finite(target.Origin) || !Finite(target.Basis.X) ||
            !Finite(target.Basis.Y) || !Finite(target.Basis.Z) || angles.Any(a => !float.IsFinite(a)))
            return new IkResult(false, (float[])initialDegrees.Clone(), float.PositiveInfinity, float.PositiveInfinity);
        float damping = .008f;
        float positionError = float.PositiveInfinity;
        float orientationError = float.PositiveInfinity;
        for (int iteration = 0; iteration < 48; ++iteration)
        {
            Transform3D current = Forward(angles, rest, tool);
            Vector3 dp = target.Origin - current.Origin;
            Vector3 dr = RotationError(target.Basis, current.Basis);
            positionError = dp.Length();
            orientationError = dr.Length();
            if (positionError <= positionTolerance && orientationError <= orientationTolerance)
                return new IkResult(true, angles, positionError, orientationError);
            double[] error = { dp.X, dp.Y, dp.Z, dr.X * RotationWeight, dr.Y * RotationWeight, dr.Z * RotationWeight };
            double[,] jacobian = new double[6, 6];
            const float epsilon = .001f;
            for (int col = 0; col < 6; ++col)
            {
                float previous = angles[col];
                angles[col] += Mathf.RadToDeg(epsilon);
                Transform3D perturbed = Forward(angles, rest, tool);
                angles[col] = previous;
                Vector3 jp = (perturbed.Origin - current.Origin) / epsilon;
                Vector3 jr = RotationError(perturbed.Basis, current.Basis) * (RotationWeight / epsilon);
                jacobian[0, col] = jp.X; jacobian[1, col] = jp.Y; jacobian[2, col] = jp.Z;
                jacobian[3, col] = jr.X; jacobian[4, col] = jr.Y; jacobian[5, col] = jr.Z;
            }
            bool improved = false;
            double oldCost = WeightedCost(dp, dr);
            for (int attempt = 0; attempt < 5; ++attempt)
            {
                double[,] system = new double[6, 6];
                for (int row = 0; row < 6; ++row)
                {
                    for (int col = 0; col < 6; ++col)
                        for (int k = 0; k < 6; ++k) system[row, col] += jacobian[row, k] * jacobian[col, k];
                    system[row, row] += damping * damping;
                }
                if (!SolveLinear(system, error, out double[] dual)) break;
                float[] step = new float[6];
                float largest = 0;
                for (int col = 0; col < 6; ++col)
                {
                    double value = 0;
                    for (int row = 0; row < 6; ++row) value += jacobian[row, col] * dual[row];
                    step[col] = (float)value;
                    largest = Math.Max(largest, Math.Abs(step[col]));
                }
                float scale = largest > .12f ? .12f / largest : 1f;
                float[] trial = new float[6];
                for (int i = 0; i < 6; ++i)
                    trial[i] = Mathf.Clamp(angles[i] + Mathf.RadToDeg(step[i] * scale), RobotController.JointMinimum[i], RobotController.JointMaximum[i]);
                Transform3D candidate = Forward(trial, rest, tool);
                double cost = WeightedCost(target.Origin - candidate.Origin, RotationError(target.Basis, candidate.Basis));
                if (double.IsFinite(cost) && cost < oldCost)
                {
                    angles = trial;
                    damping = Math.Max(.002f, damping * .6f);
                    improved = true;
                    break;
                }
                damping = Math.Min(.5f, damping * 3f);
            }
            if (!improved) break;
        }
        // A failed solve returns the input pose, never a partially solved jump.
        return new IkResult(false, (float[])initialDegrees.Clone(), positionError, orientationError);
    }

    public static Vector3 RotationError(Basis desired, Basis current)
    {
        Quaternion q = (desired.GetRotationQuaternion() * current.GetRotationQuaternion().Inverse()).Normalized();
        if (q.W < 0) q = new Quaternion(-q.X, -q.Y, -q.Z, -q.W);
        Vector3 v = new(q.X, q.Y, q.Z);
        float length = v.Length();
        if (length < .000001f) return v * 2f;
        return v * (2f * Mathf.Atan2(length, Mathf.Clamp(q.W, -1f, 1f)) / length);
    }

    private static double WeightedCost(Vector3 position, Vector3 rotation) => position.LengthSquared() + RotationWeight * RotationWeight * rotation.LengthSquared();
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool SolveLinear(double[,] matrix, double[] right, out double[] result)
    {
        var a = (double[,])matrix.Clone();
        result = (double[])right.Clone();
        for (int col = 0; col < 6; ++col)
        {
            int pivot = col;
            for (int row = col + 1; row < 6; ++row)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (!double.IsFinite(a[pivot, col]) || Math.Abs(a[pivot, col]) < 1e-12) return false;
            if (pivot != col)
            {
                for (int k = col; k < 6; ++k) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
                (result[col], result[pivot]) = (result[pivot], result[col]);
            }
            double divisor = a[col, col];
            for (int k = col; k < 6; ++k) a[col, k] /= divisor;
            result[col] /= divisor;
            for (int row = 0; row < 6; ++row)
            {
                if (row == col) continue;
                double factor = a[row, col];
                for (int k = col; k < 6; ++k) a[row, k] -= factor * a[col, k];
                result[row] -= factor * result[col];
            }
        }
        return result.All(double.IsFinite);
    }
}
