using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EstunStudio;

/// <summary>Checks real container allocation, font readability and docking motion behavior.</summary>
public partial class PendantLayoutChecks : Node
{
    private int _checks;

    public override async void _Ready()
    {
        try
        {
            var rig = new Node3D(); AddChild(rig);
            var rest = RobotKinematics.StandardRestTransforms();
            var joints = new Node3D[6];
            Node3D parent = rig;
            for (int i = 0; i < 6; i++)
            {
                joints[i] = new Node3D { Transform = rest[i] };
                parent.AddChild(joints[i]); parent = joints[i];
            }
            var tip = new Node3D { Transform = WeldTorch.ToolTransform }; parent.AddChild(tip);
            var controller = new RobotController(); AddChild(controller); controller.Initialize(joints, tip);
            controller.SetPhysicsProcess(false);
            var canvas = new CanvasLayer(); AddChild(canvas);
            var dock = new ScrollContainer
            {
                Size = new Vector2(332, 700), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
                VerticalScrollMode = ScrollContainer.ScrollMode.Auto
            };
            canvas.AddChild(dock);
            var pendant = new PendantPanel(); dock.AddChild(pendant); pendant.Build(controller);
            pendant.SetProcess(false);
            pendant.CloseRequested += dock.Hide;

            foreach (int width in new[] { 320, 328, 384 })
            {
                dock.Size = new Vector2(width, 700);
                for (int frame = 0; frame < 5; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var controls = Descendants(pendant).OfType<Control>().ToArray();
                var labels = controls.OfType<Label>().ToArray();
                Require(pendant.Scale == Vector2.One && pendant.GetGlobalTransform().Scale.IsEqualApprox(Vector2.One), "Pendant uses native pixels");
                Require(pendant.Size.X <= width + 1, $"Pendant fits a {width}px dock without horizontal scrolling");
                Require(pendant.CustomMinimumSize.Y <= 700, "Full pendant fits a 700px dock");
                Require(labels.All(l => l.GetThemeFontSize("font_size") >= 12), "All pendant labels are at least 12px");
                Require(labels.All(l => l.Size.X + 1 >= l.GetMinimumSize().X && l.Size.Y + 1 >= l.GetMinimumSize().Y), "Labels have room for their native font metrics");
                Require(controls.All(c => Contains(pendant.GetGlobalRect(), c.GetGlobalRect())), $"All controls fit the {width}px pendant");
                var jog = controls.OfType<Button>().Where(b => b.Text is "−" or "+").OrderBy(b => b.GlobalPosition.Y).ThenBy(b => b.GlobalPosition.X).ToArray();
                Require(jog.Length == 12 && jog.All(b => b.Size.X >= 48 && b.Size.Y >= 42), "Six jog rows retain large 48px targets");
                for (int i = 0; i < jog.Length; i += 2)
                    Require(jog[i].GlobalPosition.Y == jog[i + 1].GlobalPosition.Y && jog[i].GetGlobalRect().End.X <= jog[i + 1].GlobalPosition.X, "Jog pairs align without overlap");
            }

            dock.Size = new Vector2(340, 600);
            for (int frame = 0; frame < 5; frame++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(dock.GetVScrollBar().Visible, "Short windows scroll the pendant instead of shrinking its fonts");
            Require(pendant.Size.X <= 340 && pendant.Scale == Vector2.One, "Scrolling keeps native typography and available width");

            controller.SetDrives(true);
            controller.GoHome();
            Descendants(pendant).OfType<Button>().Single(b => b.Name == "HidePendant").EmitSignal(BaseButton.SignalName.Pressed);
            Require(!dock.Visible && !controller.MotionActive, "Hide button hides dock and cancels motion");
            pendant._Input(new InputEventKey { PhysicalKeycode = Key.Right, Pressed = true });
            Require(!controller.MotionActive, "Hidden pendant ignores jog keys");
            controller.GoHome();
            pendant._Input(new InputEventKey { PhysicalKeycode = Key.Space, Pressed = true });
            Require(!controller.MotionActive && controller.DrivesEnabled, "Global Space stop remains active while hidden");
            dock.Show();
            controller.GoHome();
            dock.Hide();
            Require(!controller.MotionActive, "External dock visibility changes cancel motion");
            GD.Print($"PASS: {_checks} pendant layout and docking checks");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PrintErr($"FAIL: {exception}"); GetTree().Quit(1);
        }
    }

    private static bool Contains(Rect2 outer, Rect2 inner) => outer.Grow(1).Encloses(inner);
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
        _checks++;
        if (!condition) throw new InvalidOperationException(description);
    }
}
