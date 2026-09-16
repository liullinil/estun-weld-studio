using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EstunStudio;

/// <summary>
/// Integration checks that instantiate the shipping scene, its actual procedural robot,
/// and the actual pendant controls. Run separately from pure controller checks:
/// Godot --headless --path . res://Tests/SceneChecks.tscn
/// </summary>
public partial class SceneChecks : Node
{
    private int _checks;

    public override async void _Ready()
    {
        try
        {
            // With native-pixel rendering the headless backend otherwise defaults to 64×64.
            GetWindow().Size = new Vector2I(1440, 900);
            var main = GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>();
            AddChild(main);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            var nodes = Descendants(main).ToArray();
            var model = nodes.OfType<RobotModel>().Single();
            var controller = nodes.OfType<RobotController>().Single();
            var pendant = nodes.OfType<PendantPanel>().Single();
            var ui = nodes.OfType<Control>().Single(n => n.Name == "Workspace");
            controller.SetPhysicsProcess(false);

            CheckActualRig(model, controller);
            CheckPendantLayout(pendant, ui);
            CheckPendantBindings(pendant, controller);
            CheckStopControls(pendant, controller);
            CheckHiddenInput(pendant, controller, ui);

            GD.Print($"MODEL METRICS: {model.MeshPartCount} mesh parts; {Descendants(model).Count()} nodes; " +
                $"{model.TriangleCount} triangles");
            GD.Print($"PASS: {_checks} shipping-scene integration checks");
            GetTree().Quit(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"FAIL: {ex}");
            GetTree().Quit(1);
        }
    }

    private void CheckActualRig(RobotModel model, RobotController controller)
    {
        var rest = RobotKinematics.StandardRestTransforms();
        Require(model.Joints.Length == 6 && model.Joints.All(j => j.IsInsideTree()), "Shipping robot has six live joints");
        Require(model.Joints.Select((joint, i) => joint.Position.DistanceTo(rest[i].Origin)).Max() < .000001f,
            "Actual procedural pivots match solver calibration");
        Require(model.ToolTip.Position.DistanceTo(WeldTorch.ToolTransform.Origin) < .000001f,
            "Actual visual TCP matches solver tool offset");
        Require(model.ToolTip.GetParent() == model.Joints[5], "Actual TCP follows joint six directly");
        int count = model.MeshPartCount;
        model.Build();
        Require(count == model.MeshPartCount, "Model Build is idempotent and does not duplicate visuals");
        var actualMeshes = Descendants(model).OfType<MeshInstance3D>().Where(m=>m.Name.ToString().StartsWith("CAD_Link")).ToArray();
        Require(Descendants(model).OfType<WeldTorch>().Count()==1,"Welding torch is mounted on the native flange");
        Require(actualMeshes.Length == model.MeshPartCount && model.TriangleCount > 100000,
            "Actual segmented source CAD contains detailed physical geometry");
        Require(actualMeshes.All(mesh => mesh.Name.ToString().StartsWith("CAD_Link", StringComparison.Ordinal)),
            "Every robot surface comes from the segmented CAD asset");
        Require(actualMeshes.All(mesh => mesh.GetAabb().Size.IsFinite() && mesh.GetAabb().Size.Length() < 3),
            "CAD geometry is in metres with realistic local bounds");
        var baseMeshes = actualMeshes.Where(mesh => mesh.Name.ToString().StartsWith("CAD_Link0_", StringComparison.Ordinal)).ToArray();
        Require(baseMeshes.Length > 0, "The original CAD mounting base is present");
        float baseBottom = baseMeshes.Min(mesh => mesh.GetAabb().Position.Y);
        Require(baseBottom > -.02f && baseBottom < .02f,
            "Original CAD mounting base sits on the mounting plane within source tolerance");
        CheckTransform();
        controller.SetDrives(true);
        // A multi-joint non-home pose catches parent/axis mismatch hidden by the default pose.
        for (int i = 0; i < 6; i++)
        {
            controller.SetJointJog(i, i % 2 == 0 ? 1 : -1);
            for (int frame = 0; frame < 8; frame++) controller._PhysicsProcess(1.0 / 60.0);
            controller.StopJog();
        }
        CheckTransform();
        Require(model.ToolTip.Transform.IsEqualApprox(WeldTorch.ToolTransform),
            "Calibrated torch TCP offset is unchanged during articulation");
        controller.StopMotion();

        void CheckTransform()
        {
            Transform3D actual = model.GlobalTransform.AffineInverse() * model.ToolTip.GlobalTransform;
            Require(actual.Origin.DistanceTo(controller.TcpPosition) < .00001f,
                "Real rendered chain and telemetry position agree in robot-base frame");
            Require(RobotKinematics.RotationError(actual.Basis, controller.TcpTransform.Basis).Length() < .00001f,
                "Real rendered chain and telemetry orientation agree");
        }
    }

    private void CheckPendantLayout(PendantPanel pendant, Control workspace)
    {
        var controls = Descendants(pendant).OfType<Control>().ToArray();
        var jog = controls.OfType<Button>().Where(b => b.Text is "+" or "−").ToArray();
        Require(jog.Length == 12, "Shipping pendant provides both jog directions on all six rows");
        Require(jog.All(b => Contains(pendant.GetGlobalRect(), b.GetGlobalRect())),
            "All jog hitboxes are contained by the pendant enclosure");
        var dock = pendant.GetParent<Control>();
        Require(Contains(workspace.GetGlobalRect(), dock.GetGlobalRect()),
            "Pendant dock fits the native viewport; short windows fit its contents");
        Require(jog.All(b => b.Size.X >= 43 && b.Size.Y >= 32), "Jog targets retain useful pointer hitbox sizes");
        Require(controls.OfType<Label>().Count(l => l.Text is "J1" or "J2" or "J3" or "J4" or "J5" or "J6") == 6,
            "All joint row labels are present");
        var stop = controls.OfType<Button>().Single(b => b.Name == "EmergencyStop");
        Require(Contains(pendant.GetGlobalRect(), stop.GetGlobalRect()), "Emergency stop hitbox is on screen");
    }

    private void CheckPendantBindings(PendantPanel pendant, RobotController controller)
    {
        var buttons = Descendants(pendant).OfType<Button>().Where(b => b.Text is "+" or "−")
            .OrderBy(b => b.GlobalPosition.Y).ThenBy(b => b.GlobalPosition.X).ToArray();
        controller.SetDrives(true);
        for (int axis = 0; axis < 6; axis++)
        for (int directionIndex = 0; directionIndex < 2; directionIndex++)
        {
            var button = buttons[axis * 2 + directionIndex];
            float[] before = controller.AnglesDegrees;
            button.EmitSignal(BaseButton.SignalName.ButtonDown);
            for (int frame = 0; frame < 3; frame++) controller._PhysicsProcess(1.0 / 60.0);
            button.EmitSignal(BaseButton.SignalName.ButtonUp);
            float[] after = controller.AnglesDegrees;
            int direction = directionIndex == 0 ? -1 : 1;
            Require((after[axis] - before[axis]) * direction > .00001f &&
                before.Where((_, i) => i != axis).SequenceEqual(after.Where((_, i) => i != axis)),
                $"Pendant J{axis + 1} {button.Text} controls its physical joint and direction without Space");
            Require(!controller.MotionActive, "Pendant button release cancels the actual jog");
        }
        foreach (string frame in new[] { "WORLD", "TOOL" })
        {
            var tab = Descendants(pendant).OfType<Button>().Single(b => b.Text == frame);
            tab.EmitSignal(BaseButton.SignalName.Pressed);
            var labels = Descendants(pendant).OfType<Label>().ToArray();
            Require(labels.Count(l => l.Text is "X" or "Y" or "Z" or "A" or "B" or "C") == 6,
                $"{frame} tab exposes all six Cartesian controls");
            Require(labels.Count(l => l.Text == "mm") == 3, $"{frame} position rows show millimeter units");
        }
        Descendants(pendant).OfType<Button>().Single(b => b.Text == "JOINT").EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void CheckStopControls(PendantPanel pendant, RobotController controller)
    {
        controller.SetDrives(true);
        var jog = Descendants(pendant).OfType<Button>().First(b => b.Text == "+");
        jog.EmitSignal(BaseButton.SignalName.ButtonDown);
        Require(controller.MotionActive, "Pointer jog starts directly with drives on");
        pendant._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        Require(!controller.MotionActive, "Mouse release outside jog controls stops the active jog");
        jog.EmitSignal(BaseButton.SignalName.ButtonDown);
        jog.EmitSignal(Control.SignalName.MouseExited);
        Require(!controller.MotionActive, "Dragging the pointer out of a jog control stops motion");
        jog.EmitSignal(BaseButton.SignalName.ButtonDown);
        pendant._Process(.05);
        Require(!controller.MotionActive, "Pointer release polling clears a jog if its release event was missed");

        KeyInput(pendant, Key.Right, true);
        Require(controller.MotionActive, "Arrow jogging starts without Space");
        KeyInput(pendant, Key.Right, false);
        Require(!controller.MotionActive, "Releasing the arrow stops keyboard jogging");
        KeyInput(pendant, Key.Right, true);
        pendant._Process(.05);
        Require(!controller.MotionActive, "Arrow release polling clears a jog if its release event was missed");

        Button home = Descendants(pendant).OfType<Button>().Single(b=>b.Name=="GoHome");
        home.EmitSignal(BaseButton.SignalName.Pressed);
        Require(controller.MotionActive, "HOME click starts automatic movement with no Space hold");
        KeyInput(pendant, Key.Space, false);
        pendant._Input(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false });
        pendant._Process(.05);
        Require(controller.MotionActive, "Space release and an unrelated mouse release do not stop HOME");
        KeyInput(pendant, Key.Space, true);
        Require(!controller.MotionActive && controller.DrivesEnabled, "Space press stops HOME while keeping drives enabled");
        KeyInput(pendant, Key.Space, false);
        controller._PhysicsProcess(.05);
        Require(!controller.MotionActive, "Releasing the stop shortcut does not restart motion");

        controller.ApplyPlannedPose(controller.AnglesDegrees, "Welding simulation test");
        KeyInput(pendant, Key.Space, true);
        Require(!controller.IsPlannedMotion && !controller.MotionActive, "Space stops the external welding-motion session");
        KeyInput(pendant, Key.Space, false);
        home.EmitSignal(BaseButton.SignalName.Pressed);
        pendant._Notification((int)NotificationApplicationFocusOut);
        pendant._Notification((int)NotificationApplicationFocusIn);
        pendant._Process(.05); controller._PhysicsProcess(.05);
        Require(!controller.MotionActive && controller.DrivesEnabled, "Pendant focus loss cancels HOME and focus return cannot resume it");


    }

    private void CheckHiddenInput(PendantPanel pendant, RobotController controller, Control workspace)
    {
        controller.SetDrives(true);
        controller.StopMotion();
        workspace.Hide();
        KeyInput(pendant, Key.Right, true);
        Require(!controller.MotionActive,
            "Cinema mode ignores jogging through the invisible pendant");
        controller.GoHome();
        KeyInput(pendant, Key.Space, true);
        Require(!controller.MotionActive && controller.DrivesEnabled,
            "Space remains a global motion stop while the pendant is hidden");
        KeyInput(pendant, Key.Escape, true);
        Require(controller.EmergencyStopped && !controller.DrivesEnabled,
            "Emergency stop remains global while the pendant is hidden");
        KeyInput(pendant, Key.Right, false);
        KeyInput(pendant, Key.Space, false);
        workspace.Show();
        controller.ResetEmergencyStop();
        controller.SetDrives(true);
        Require(!controller.MotionActive, "Returning from cinema does not resume cancelled motion");
    }

    private static void KeyInput(PendantPanel pendant, Key code, bool pressed) => pendant._Input(new InputEventKey {
        Keycode = code, PhysicalKeycode = code, Pressed = pressed
    });

    private static bool Contains(Rect2 outer, Rect2 inner) =>
        inner.Position.X >= outer.Position.X - 1 && inner.Position.Y >= outer.Position.Y - 1 &&
        inner.End.X <= outer.End.X + 1 && inner.End.Y <= outer.End.Y + 1;

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }

    private void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        GD.Print($"  OK  {description}");
    }
}
