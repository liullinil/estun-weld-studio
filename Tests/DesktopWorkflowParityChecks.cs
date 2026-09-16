using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>Shipping desktop workflow: explicit assignment, shared postprocessing, GUI state and warning playback.</summary>
public partial class DesktopWorkflowParityChecks : Node
{
    private int _checks;
    private void Require(bool value, string description) { if (!value) throw new InvalidOperationException(description); _checks++; GD.Print("  OK  " + description); }
    public override async void _Ready()
    {
        try
        {
            SelectionChecks();
            GetWindow().Size = new Vector2I(1440, 900);
            var main = GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>(); AddChild(main); await Frame();
            var nodes = Descendants(main).ToArray(); var workspace = nodes.OfType<WeldingWorkspace>().Single(); var controller = nodes.OfType<RobotController>().Single();
            var watch = Stopwatch.StartNew(); while (workspace.Part == null || workspace.IsBusy) { if (watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("Sample import"); await Frame(); }
            workspace.SetPhysicsProcess(false); controller.SetPhysicsProcess(false); main.SetProcess(false);
            var part = workspace.Part!; var gizmo = nodes.OfType<TransformGizmo>().Single();
            Require(part.Candidates().Count >= 11 && !part.ConcaveOnly && part.Selection.Selected.Count == 0, "Sample candidates require explicit assignment and concave filter defaults off");
            Require(nodes.OfType<ScrollContainer>().Count() == 1, "Only the seam list owns a scroll container");
            var simulate = nodes.OfType<Button>().Single(b => b.Text == "Simulate"); Require(simulate.Disabled, "Simulation is unavailable before generation");
            workspace.ShowWorkpieceGizmo = false; Require(!gizmo.Active, "Workpiece gizmo toggle disables all drawing and hit regions"); workspace.ShowWorkpieceGizmo = true; Require(gizmo.Active, "Gizmo can be restored for manual editing");
            var contact = part.Candidates().First(s => s.Id == "S002"); part.Select(contact.Id);
            Require(part.AssignedSeams().Count == 1 && part.AssignedSeams()[0] == contact, "Only selected candidate enters the assignment");
            part.SetResult(new PlannedSeam { Id = contact.Id, State = WeldSeamState.Warning, Reason = "Torch collides with workpiece", HasCollision = true }); part.RefreshSeams();
            Require(part.IsWarning(contact.Id) && part.WarningHint(contact.Id).Contains("workpiece"), "Selected problem seam is red with the precise reason");
            part.Select(contact.Id); Require(!part.IsWarning(contact.Id) && part.WarningHint(contact.Id) == "" && part.Result(contact.Id) != null, "Deselection removes warning color and hint while retaining metadata");
            part.Select(contact.Id); Require(part.IsWarning(contact.Id), "Reassignment restores the known warning");
            workspace.DisableWarnings(); Require(part.Selection.Selected.Count == 0 && part.Candidates().Contains(contact), "Disable warnings deselects without deleting candidates");
            workspace.SelectAll(); Require(part.AssignedSeams().Count == part.Candidates().Count, "SelectAll assigns every eligible candidate"); workspace.UnselectAll(); Require(part.AssignedSeams().Count == 0, "unSelectAll preserves every candidate");
            part.Select(contact.Id); workspace.Generate(); Require(workspace.IsBusy && !gizmo.Active, "Generation reports busy and disables manipulation"); await Finish(workspace.PlanningTask);
            Require(workspace.Program?.Motions.Count > 0 && workspace.Program.ReadyCount == 1 && !simulate.Disabled, "Shared native planner and postprocessor produce an executable selected seam");
            Require(Descendants(main).OfType<Node3D>().Any(n => n.Name == "Calculated tool path" && n.Visible), "Calculated rapid and working path is visible before simulation");
            string export = ProjectSettings.GlobalizePath("user://desktop-parity.lua"); await Finish(workspace.SaveLuaAsync(export, true, 12)); string lua = System.IO.File.ReadAllText(export);
            Require(lua.Contains("setDO(12,1)") && lua.Contains("setDO(12,0)") && !lua.Contains("movC("), "Lua export applies torch IO and linearized arcs options");
            workspace.Run(); Require(workspace.IsSimulating && controller.AutomaticMode && !part.OverlaysVisible && !gizmo.Active, "Simulation switches AUTO and hides all seam/gizmo overlays");
            Require(!controller.SetJointJog(0, 1) && !controller.GoHome(), "AUTO rejects manual jogging and Go Home");
            workspace.Pause(); float[] frozen = controller.AnglesDegrees; workspace._PhysicsProcess(.05);
            Require(workspace.IsPaused && workspace.IsSimulating && controller.AutomaticMode && frozen.SequenceEqual(controller.AnglesDegrees) && !part.OverlaysVisible, "Paused program retains AUTO, hides seams and freezes position");
            controller.SpeedOverride = .9f; workspace.Pause(); workspace._PhysicsProcess(.05); Require(!workspace.IsPaused, "Resume retains adjustable speed");
            workspace.Stop(); Require(!workspace.IsSimulating && !controller.AutomaticMode && workspace.Program == null && simulate.Disabled, "Stop returns MANUAL and requires regeneration");
            workspace.Generate(); workspace.CancelGeneration(); await Finish(workspace.PlanningTask); Require(!workspace.IsBusy && workspace.Program == null, "Explicit Cancel aborts generation without publishing a program");
            workspace.UnselectAll(); part.Select("S007"); workspace.Generate(); await Finish(workspace.PlanningTask);
            Require(workspace.Program?.WarningCount > 0 && workspace.Program.Seams.Where(s => s.State == WeldSeamState.Warning).All(s => s.Reason.Length > 0 && workspace.Program.Motions.Any(m => m.SeamId == s.Id)), "Collision warnings remain executable with explicit reasons in the actual sample");
            workspace.Run(); bool sawContacts = false; int ticks = 0;
            controller.SpeedOverride = 1;
            while (workspace.IsSimulating && ticks++ < 30000)
            {
                workspace._PhysicsProcess(.05);
                if (workspace.CollisionLinks.Length > 0) { sawContacts = workspace.CollisionPairs.Length > 0; break; }
            }
            Require(sawContacts && workspace.IsSimulating, "Warning playback exposes colliding links and object pairs without stopping simulation");
            workspace.Stop();
            var protractor = nodes.OfType<AngleRangeControl>().Single(); protractor.Size = new Vector2(300, 130); protractor.SetRange(85, 95);
            protractor._Input(new InputEventMouseMotion { Position = new Vector2(150, 10) }); Require(!protractor.IsDragging && protractor.Minimum == 85, "Hover never starts a protractor drag");
            Vector2 centre = new(150, 110); float radius = 80; Vector2 handle = centre + new Vector2(-Mathf.Cos(Mathf.DegToRad(85)), -Mathf.Sin(Mathf.DegToRad(85))) * radius;
            protractor._GuiInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = handle }); Require(protractor.IsDragging, "Protractor drag begins only from explicit handle press");
            protractor._Input(new InputEventMouseMotion { Position = protractor.GlobalPosition + new Vector2(-900, -500) }); Require(protractor.Minimum < 60, "Captured protractor handle moves far outside the control");
            protractor._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = new Vector2(-1500, -1500) }); Require(!protractor.IsDragging, "Mouse release anywhere ends dragging");
            GD.Print($"PASS: {_checks} desktop parity workflow checks"); GetTree().Quit(0);
        }
        catch (Exception error) { GD.PrintErr("FAIL: " + error); GetTree().Quit(1); }
    }
    private void SelectionChecks()
    {
        CadSeam Edge(string id, Vector3 a, Vector3 b) => new() { Id = id, Points = new[] { a, b }, MinAngleDegrees = 90, MaxAngleDegrees = 90, AngleDegrees = 90 };
        var source = new[] { Edge("A", Vector3.Zero, Vector3.Right), Edge("B", Vector3.Right, Vector3.Right * 2), Edge("C", Vector3.Right, Vector3.Right + Vector3.Up), Edge("D", Vector3.Right * 2, Vector3.Right * 3), Edge("E", Vector3.Right + Vector3.Up, Vector3.Right + Vector3.Up * 2), Edge("Separate", Vector3.One * 10, Vector3.One * 11) };
        var selection = new SeamSelection(); selection.SetCandidates(source);
        Require(selection.Component("A").SequenceEqual(new[] { "A", "B", "C", "D", "E" }), "Propagation orders nearest graph neighbors before farther neighbors deterministically");
        selection.Begin("A"); for (int count = 0; count < 5; count++) { selection.ApplyScope(count); Require(selection.Selected.Count == count + 1, "Propagation adds exactly one candidate at each slider step"); }
        selection.Begin("A"); Require(selection.Transaction!.Scope == 4 && selection.Selected.Count == 0, "Removal restores the scope used for addition");
        selection.ApplyScope(1); Require(selection.Selected.SetEquals(new[] { "C", "D", "E" }), "Reducing removal scope restores unaffected assignment from transaction snapshot");
        selection.Finish(); selection.SetCandidates(source.Take(2).ToArray()); Require(selection.Selected.Count == 0, "Candidates excluded by detection filters leave the work assignment");
    }
    private async Task Finish(Task task) { var watch = Stopwatch.StartNew(); while (!task.IsCompleted) { if (watch.Elapsed.TotalSeconds > 150) throw new TimeoutException("Workflow operation"); await Frame(); } await task; }
    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    private static IEnumerable<Node> Descendants(Node parent) { foreach (Node child in parent.GetChildren()) { yield return child; foreach (Node nested in Descendants(child)) yield return nested; } }
}
