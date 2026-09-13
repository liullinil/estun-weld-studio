using Godot;
using System;
using System.Linq;

namespace EstunStudio;

/// <summary>Run with Godot --headless --path . res://Tests/RobotChecks.tscn.</summary>
public partial class RobotChecks : Node
{
    private int _checks;

    public override void _Ready()
    {
        try
        {
            CheckKinematics();
            CheckControllerInterlocks();
            GD.Print($"PASS: {_checks} robot kinematics and interlock checks");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"FAIL: {ex}");
            GetTree().Quit(1);
        }
    }

    private void CheckKinematics()
    {
        var rest = RobotKinematics.StandardRestTransforms();
        var tool = RobotKinematics.StandardToolTransform;
        Transform3D zero = RobotKinematics.Forward(new float[6], rest, tool);
        Require(zero.Origin.DistanceTo(new Vector3(-.850f, 1.1435f, -.3815f)) < .00001f, "ENCY S20-180 Pro zero-angle flange coordinates");
        Basis zeroBasis = new(Vector3.Left, Vector3.Back, Vector3.Up);
        Require(RobotKinematics.RotationError(zeroBasis, zero.Basis).Length() < .00001f, "ENCY zero-angle flange orientation");
        Transform3D cadPose = RobotKinematics.Forward(RobotController.CadPoseAngles, rest, tool);
        Require(cadPose.Origin.DistanceTo(new Vector3(0, 1.9935f, -.3815f)) < .00001f, "Native CAD assembly flange agrees with source millimetre dimensions");
        Transform3D homePose = RobotKinematics.Forward(RobotController.HomeAngles, rest, tool);
        Require(homePose.Origin.DistanceTo(new Vector3(.9275f, .9035f, -.219f)) < .00001f, "ENCY saved home pose preserves shoulder and wrist offsets");
        Require(RobotController.JointMinimum.SequenceEqual(new float[] {-360,-360,-160,-360,-360,-360}) &&
            RobotController.JointMaximum.SequenceEqual(new float[] {360,360,160,360,360,360}), "Joint travel matches manufacturer S-Pro specification");

        float[][] poses =
        {
            RobotController.HomeAngles,
            new float[] { 25, -40, -25, 30, 40, -35 },
            new float[] { -50, 15, -70, -25, 55, 80 },
            new float[] { 80, -55, -10, 70, -45, -110 }
        };
        foreach (float[] pose in poses)
        {
            Transform3D target = RobotKinematics.Forward(pose, rest, tool);
            float[] seed = pose.Select((v, i) => v + (i % 2 == 0 ? 3f : -3f)).ToArray();
            var result = RobotKinematics.Solve(target, seed, rest, tool);
            Require(result.Success, $"IK converges to nearby reachable pose [{string.Join(",", pose)}]");
            Transform3D reached = RobotKinematics.Forward(result.AnglesDegrees, rest, tool);
            Require(reached.Origin.DistanceTo(target.Origin) < .0003f, "IK position error below 0.3 mm");
            Require(RobotKinematics.RotationError(target.Basis, reached.Basis).Length() < .0012f, "IK orientation error below 0.07 degree");
        }

        // Independent Cartesian perturbations exercise all six Jacobian rows.
        float[] seedPose = { 25, -40, -25, 30, 40, -35 };
        Transform3D start = RobotKinematics.Forward(seedPose, rest, tool);
        Vector3[] axes = { Vector3.Right, Vector3.Up, Vector3.Back };
        for (int i = 0; i < 6; i++)
        {
            Transform3D target = start;
            if (i < 3) target.Origin += axes[i] * .002f;
            else target.Basis = new Basis(axes[i - 3], .005f) * target.Basis;
            var result = RobotKinematics.Solve(target, seedPose, rest, tool, .00003f, .0001f);
            Require(result.Success, $"Full 6DOF Cartesian perturbation axis {i + 1}");
            Require(result.AnglesDegrees.Zip(seedPose, (a, b) => Math.Abs(a - b)).Max() < 5f, "Local Cartesian solve has no large joint jump");
        }

        Transform3D unreachable = start;
        unreachable.Origin = new Vector3(100, 100, 100);
        var failure = RobotKinematics.Solve(unreachable, seedPose, rest, tool);
        Require(!failure.Success, "Unreachable target rejected");
        Require(failure.AnglesDegrees.SequenceEqual(seedPose), "Failed solve preserves original pose");
        unreachable.Origin = new Vector3(float.NaN, 0, 0);
        failure = RobotKinematics.Solve(unreachable, seedPose, rest, tool);
        Require(!failure.Success && failure.AnglesDegrees.SequenceEqual(seedPose), "Nonfinite target rejected without corrupting pose");

        var halfTurn = new Basis(Vector3.Up, Mathf.Pi);
        Require(Math.Abs(RobotKinematics.RotationError(halfTurn, Basis.Identity).Length() - Mathf.Pi) < .0001f, "180-degree orientation error is not lost");
    }

    private void CheckControllerInterlocks()
    {
        var root = new Node3D();
        AddChild(root);
        var joints = new Node3D[6];
        var rest = RobotKinematics.StandardRestTransforms();
        for (int i = 0; i < 6; ++i)
        {
            joints[i] = new Node3D { Transform = rest[i] };
            if (i == 0) root.AddChild(joints[i]); else joints[i - 1].AddChild(joints[i]);
        }
        var tip = new Node3D { Transform = RobotKinematics.StandardToolTransform };
        joints[5].AddChild(tip);
        var controller = new RobotController();
        AddChild(controller);
        controller.Initialize(joints, tip);
        controller.SetPhysicsProcess(false);
        Require(!controller.DrivesEnabled && !controller.HoldToRun && !controller.EmergencyStopped, "Startup has drives and enable off");
        Require(controller.TcpPosition.DistanceTo(tip.GlobalPosition) < .00001f, "Reported FK matches rendered nested pivot chain");
        Require(!controller.SetJointJog(0, 1), "Jog rejected with drives off");
        controller.SetDrives(true);
        Require(!controller.SetJointJog(0, 1), "Jog rejected without enabling switch");
        controller.HoldToRun = true;
        Require(controller.SetJointJog(0, 1), "Jog accepted with both interlocks");
        float[] before = controller.AnglesDegrees;
        Tick(controller, 30);
        Require(controller.AnglesDegrees[0] > before[0], "Joint jog integrates velocity");
        controller.HoldToRun = false;
        before = controller.AnglesDegrees;
        Tick(controller, 30);
        Require(controller.AnglesDegrees.SequenceEqual(before) && !controller.MotionActive, "Enable release stops and cancels motion");
        controller.HoldToRun = true;
        controller.SetJointJog(0, -1);
        controller.EmergencyStop();
        before = controller.AnglesDegrees;
        Tick(controller, 30);
        Require(controller.EmergencyStopped && !controller.DrivesEnabled && !controller.HoldToRun, "Emergency stop disables drives and enabling switch");
        Require(controller.AnglesDegrees.SequenceEqual(before), "Emergency stop prevents motion");
        Require(!controller.SetDrives(true), "Latched emergency stop prevents drive re-enable");
        controller.ResetEmergencyStop();
        Require(!controller.EmergencyStopped && !controller.DrivesEnabled, "Reset clears latch without automatically enabling drives");

        controller.SetDrives(true);
        controller.HoldToRun = true;
        controller.SpeedOverride = 1f;
        controller.SetJointJog(0, 1);
        Tick(controller, 400);
        Require(Math.Abs(controller.AnglesDegrees[0] - RobotController.JointMaximum[0]) < .00001f && !controller.MotionActive, "Positive joint limit clamps and stops jog");
        controller.SetJointJog(0, -1);
        Tick(controller, 700);
        Require(Math.Abs(controller.AnglesDegrees[0] - RobotController.JointMinimum[0]) < .00001f, "Negative joint limit clamps jog");
        Require(controller.GoHome(), "Home accepted with interlocks");
        Tick(controller, 1000);
        Require(controller.AnglesDegrees.Zip(RobotController.HomeAngles, (a, b) => Math.Abs(a - b)).Max() < .0001f && !controller.MotionActive, "Home interpolates to exact home pose and completes");
        controller.RecordWaypoint();
        controller.SetJointJog(0, 1);
        Tick(controller, 30);
        controller.StopJog();
        controller.RecordWaypoint();
        Require(controller.Waypoints.Count == 2, "Waypoint recording retains distinct robot poses");
        float[] finalPose = controller.AnglesDegrees;
        Require(controller.PlayWaypoints(), "Program accepted with interlocks");
        Tick(controller, 1500);
        Require(controller.AnglesDegrees.Zip(finalPose, (a, b) => Math.Abs(a - b)).Max() < .0001f && !controller.IsPlaying, "Program executes all waypoints and stops at final point");
        controller.GoHome();
        Tick(controller, 1000);
        Require(controller.SetTcpJog(1, 1, false), "Cartesian jog accepted with interlocks");
        Vector3 cartesianStart = controller.TcpPosition;
        Tick(controller, 5);
        controller.StopJog();
        Require(controller.LastIkSucceeded && controller.TcpPosition.Y > cartesianStart.Y + .001f, "TCP BASE Y jog moves tool with IK");
        controller.GoHome();
        Tick(controller, 1000);
        Transform3D toolStart = controller.TcpTransform;
        controller.SetTcpJog(0, 1, true);
        Tick(controller, 5);
        controller.StopJog();
        Vector3 toolTravel = controller.TcpPosition - toolStart.Origin;
        Require(controller.LastIkSucceeded && toolTravel.Dot(toolStart.Basis.X) > .001f, "TCP TOOL X jog follows tool axis");
        Require(toolTravel.Cross(toolStart.Basis.X).Length() < .0003f, "Tool-frame translation preserves perpendicular TCP coordinates");
        Transform3D orientationStart = controller.TcpTransform;
        controller.SetTcpJog(3, 1, true);
        Tick(controller, 5);
        controller.StopJog();
        Require(controller.LastIkSucceeded && controller.TcpPosition.DistanceTo(orientationStart.Origin) < .0003f, "Tool orientation jog preserves TCP position");
        Require(RobotKinematics.RotationError(controller.TcpTransform.Basis, orientationStart.Basis).Dot(orientationStart.Basis.X) > .005f, "Tool orientation jog rotates about tool axis");
        controller.SetJointJog(1, 1);
        controller._Notification((int)NotificationApplicationFocusOut);
        before = controller.AnglesDegrees;
        Tick(controller, 30);
        Require(!controller.HoldToRun && !controller.MotionActive && controller.AnglesDegrees.SequenceEqual(before), "Application focus loss cancels motion and enabling switch");
        CheckPersistence(controller);
        controller.SpeedOverride = float.NaN;
        Require(float.IsFinite(controller.SpeedOverride), "Nonfinite override rejected");
        controller.SpeedOverride = 500;
        Require(controller.SpeedOverride == 1f, "Override clamps to 100 percent");
        controller.HoldToRun = false;
        Require(!controller.GoHome() && !controller.PlayWaypoints(), "Home and program obey enable interlock");
        controller.Free();
        root.Free();
    }

    private void CheckPersistence(RobotController controller)
    {
        string path = controller.WaypointFilePath;
        bool existed = Godot.FileAccess.FileExists(path);
        string? backup = existed ? Godot.FileAccess.GetFileAsString(path) : null;
        try
        {
            controller.ClearWaypoints();
            controller.RecordWaypoint();
            float[] savedPose = controller.AnglesDegrees;
            Require(controller.SaveWaypoints(), "Waypoint program serializes to local JSON");
            controller.ClearWaypoints();
            Require(controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Saved program loads with correct point count");
            Require(controller.Waypoints[0].AnglesDegrees.SequenceEqual(savedPose), "JSON round-trip preserves all six joint values");
            WriteFile(path, "{}");
            Require(!controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Incomplete JSON schema rejected and existing program retained");
            WriteFile(path, "{\"Version\":1,\"RobotModel\":\"ESTUN S20-180 Pro / ENCY\",\"Points\":[{\"Name\":\"Bad\",\"AnglesDegrees\":[999,0,0,0,0,0],\"SpeedOverride\":0.5}]}");
            Require(!controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Out-of-limit program rejected before replacing valid program");
            WriteFile(path, "{\"Version\":1,\"RobotModel\":\"ESTUN S20-180 Pro / ENCY\",\"Points\":[null]}");
            Require(!controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Null program point rejected safely");
            WriteFile(path, "{\"Version\":1,\"RobotModel\":\"Other robot\",\"Points\":[]}");
            Require(!controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Programs for a different mechanical model are rejected");
            WriteFile(path, "corrupt");
            Require(!controller.LoadWaypoints() && controller.Waypoints.Count == 1, "Malformed JSON rejected and current program retained");
        }
        finally
        {
            if (existed) WriteFile(path, backup!);
            else DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
        }
    }

    private static void WriteFile(string path, string text)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Write);
        if (file == null) throw new InvalidOperationException("Could not create persistence test file");
        file.StoreString(text);
    }

    private static void Tick(RobotController controller, int count)
    {
        for (int i = 0; i < count; i++) controller._PhysicsProcess(1.0 / 60.0);
    }

    private void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        GD.Print($"  OK  {description}");
    }
}
