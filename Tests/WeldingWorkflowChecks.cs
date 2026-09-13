using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>Shipping CAD workspace end-to-end: actual STEP, planning, interlocks and stale-program invalidation.</summary>
public partial class WeldingWorkflowChecks : Node
{
    private int _checks;
    private void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++; GD.Print("  OK  " + message);
    }

    public override async void _Ready()
    {
        try
        {
            var main = GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>();
            AddChild(main);
            await Frame();
            var nodes = Descendants(main).ToArray();
            var workspace = nodes.OfType<WeldingWorkspace>().Single();
            var controller = nodes.OfType<RobotController>().Single();
            var gizmo = nodes.OfType<TransformGizmo>().Single();
            var camera = nodes.OfType<Camera3D>().Single();
            var panel = nodes.OfType<Panel>().Single(n => n.Name == "WeldingWorkspace");
            controller.SetPhysicsProcess(false);
            workspace.SetPhysicsProcess(false);
            var messages = new List<string>();
            workspace.Toast += messages.Add;

            Task import = workspace.Import(ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"));
            Require(workspace.IsBusy, "Actual workspace enters asynchronous STEP import state");
            await Finish(import, "STEP import");
            Require(workspace.Part != null && !workspace.IsBusy, "STEP import commits a scene workpiece and releases busy state");
            var part = workspace.Part!;
            Require(part.Document.SolidCount == 3 && part.Candidates().Count >= 8, "Imported assembly exposes actual inter-part seams");
            Require(part.GlobalPosition.Y >= .26f && part.GlobalPosition.Y < .29f, "Default workpiece placement matches collision-planning cell frame");
            // A short physical contact seam keeps this an end-to-end workflow test,
            // while the separate WeldChecks validates all candidates and every motion.
            foreach (var seam in part.Document.Seams) seam.Deleted = seam.Id != "S002";
            part.RefreshSeams();
            Require(part.Candidates().Count == 1, "Operator selection narrows planning to one real 8 mm contact");

            workspace.Generate();
            Task firstPlan = workspace.PlanningTask;
            Require(workspace.IsBusy && !gizmo.Active, "Planning locks workpiece manipulation");
            controller.SetDrives(true); controller.HoldToRun = true;
            workspace.Run();
            Require(!workspace.IsSimulating, "SIMULATE cannot start while planning is busy");
            workspace._UnhandledInput(new InputEventKey { Keycode = Key.W, Pressed = true });
            Require(!gizmo.Active, "W shortcut cannot reactivate gizmo during planning");
            await Finish(firstPlan, "First physical seam plan");
            Require(workspace.Program is { ReadyCount: 1 } && workspace.Program.Motions.Any(m => m.ArcOn), "Actual workspace generates collision-checked travel and welding motions");
            Require(!workspace.IsBusy, "Successful planning releases busy state");
            var originalProgram = workspace.Program!;

            controller.HoldToRun = false;
            workspace.Run();
            Require(!workspace.IsSimulating, "Enable release blocks playback start");
            controller.HoldToRun = true;
            var changed = (float[])originalProgram.StartAngles.Clone(); changed[0] += 1;
            controller.ApplyPlannedPose(changed, "Workflow setup"); controller.StopMotion();
            workspace.Run();
            Require(!workspace.IsSimulating && messages.Last().Contains("start pose changed"), "Moved robot start pose invalidates playback eligibility");
            controller.ApplyPlannedPose(originalProgram.StartAngles, "Workflow setup"); controller.StopMotion();
            workspace.Run();
            Require(workspace.IsSimulating && controller.IsPlannedMotion, "Valid interlocks start the actual generated program");
            workspace._PhysicsProcess(.016);
            controller.EmergencyStop(); workspace._PhysicsProcess(.016);
            Require(!workspace.IsSimulating && !controller.MotionActive && !controller.DrivesEnabled, "E-stop halts workspace playback and clears controller motion");
            Require(!Descendants(workspace.Effects).OfType<OmniLight3D>().Any(l => l.Visible), "E-stop extinguishes the welding arc light");
            controller.ResetEmergencyStop(); controller.SetDrives(true); controller.HoldToRun = true;
            controller.ApplyPlannedPose(originalProgram.StartAngles, "Workflow setup"); controller.StopMotion();
            workspace.Run(); controller.HoldToRun = false; workspace._PhysicsProcess(.016);
            Require(!workspace.IsSimulating && !controller.MotionActive, "Releasing enable stops welding without queued continuation");

            // Regeneration starts with an existing valid program: this previously
            // allowed old motions to run concurrently with a new collision worker.
            controller.SetDrives(true); controller.HoldToRun = true;
            controller.ApplyPlannedPose(originalProgram.StartAngles, "Workflow setup"); controller.StopMotion();
            workspace.Generate(); Task replan = workspace.PlanningTask;
            Require(workspace.IsBusy && workspace.Program == null, "Regeneration discards the prior executable program immediately");
            workspace.Run();
            Require(!workspace.IsSimulating, "Prior program cannot run concurrently with regeneration");
            workspace.Generate(); // The generate button acts as cancel during a plan.
            await Finish(replan, "Cancelled regeneration");
            Require(!workspace.IsBusy && workspace.Program == null, "Cancelled planning never publishes a stale result");

            workspace.Generate(); await Finish(workspace.PlanningTask, "Plan before seam edit");
            Require(workspace.Program?.ReadyCount == 1, "Workflow can generate again after cancellation");
            part.Select("S002");
            workspace._UnhandledInput(new InputEventKey { Keycode = Key.Delete, Pressed = true });
            Require(workspace.Program == null && part.Document.Seams.Single(s => s.Id == "S002").Deleted, "Deleting selected seam invalidates its generated program");
            part.Document.Seams.Single(s => s.Id == "S002").Deleted = false; part.RefreshSeams();
            workspace.Generate(); await Finish(workspace.PlanningTask, "Plan before gizmo edit");
            Require(workspace.Program?.ReadyCount == 1, "Restored physical seam produces a new program");

            main.SetProcess(false); // Hold this actual camera still for projected input.
            camera.GlobalPosition = part.GlobalPosition + new Vector3(2, 2, 3);
            camera.HOffset = 0; camera.LookAt(part.GlobalPosition);
            gizmo.RotationMode = false; gizmo.Active = true; await Frame();
            float length = camera.GlobalPosition.DistanceTo(part.GlobalPosition) * .075f;
            Vector3 picked = part.GlobalPosition + Vector3.Right * length * .8f;
            Require(gizmo.TryBeginDrag(camera.UnprojectPosition(picked)), "Actual workspace gizmo accepts a projected world-axis handle");
            Vector3 before = part.GlobalPosition;
            gizmo._Input(new InputEventMouseMotion { Position = camera.UnprojectPosition(picked + Vector3.Right * .02f) });
            gizmo._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
            Require(part.GlobalPosition.DistanceTo(before + Vector3.Right * .02f) < .0002f && workspace.Program == null, "20 mm interactive translation invalidates the previous checked trajectory");
            Require(!workspace.IsSimulating, "Part editing leaves robot motion stopped");

            GD.Print($"PASS: {_checks} shipping welding-workspace workflow checks");
            GetTree().Quit(0);
        }
        catch (Exception exception) { GD.PrintErr("FAIL: " + exception); GetTree().Quit(1); }
    }

    private async Task Finish(Task task, string operation)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException(operation + " exceeded 120 seconds");
            await Frame();
        }
        await task;
    }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (Node child in parent.GetChildren()) { yield return child; foreach (Node nested in Descendants(child)) yield return nested; }
    }
}
