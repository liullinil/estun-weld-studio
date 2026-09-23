using Godot;
using System;
using System.Linq;
using System.Threading;

namespace EstunStudio;

public partial class WarningPlannerChecks : Node
{
    private int _checks;
    private void Require(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); _checks++; }

    public override void _Ready()
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            Transform3D home = RobotKinematics.Forward(RobotController.HomeAngles, RobotKinematics.StandardRestTransforms(), WeldTorch.ToolTransform);
            Vector3 start = home.Origin - home.Basis.Y * .003f;
            CadSeam Seam(params Vector3[] points) => new() { Id = "Warning test", Points = points,
                NormalA = (home.Basis.Y + home.Basis.Z).Normalized(), NormalB = (home.Basis.Y - home.Basis.Z).Normalized() };
            var seam = Seam(start, start + home.Basis.X * .024f);
            var collision = new CollisionScene(new BoxMesh { Size = Vector3.One * .2f }.GetFaces(),
                new[] { new RobotCapsule(0, Vector3.Zero, Vector3.Up * .02f, .03f) });
            WeldPlanRequest Request(bool warning, CollisionScene scene, CadSeam candidate, float spacing = .008f) => new()
            { Seams = new[] { candidate }, ToolTransform = WeldTorch.ToolTransform, Collision = scene, AllowWarningPaths = warning, SampleSpacing = spacing };

            WeldProgram strict = WeldPlanner.Plan(Request(false, collision, seam), cancellation.Token);
            Require(strict.Motions.Count == 0 && strict.BlockedCount == 1, "Default strict planner rejects colliding start pose");
            int safeOrientationAttempts = 0;
            WeldProgram preview = WeldPlanner.Plan(Request(true, collision, seam), cancellation.Token,
                (_, message) => { if (message.Contains(" · orientation ")) safeOrientationAttempts++; });
            Require(preview.WarningCount == 1 && preview.BlockedCount == 0 && preview.Motions.Any(m => m.ArcOn), "Collision preview traverses warning seam");
            Require(preview.Seams[0].HasCollision && preview.Seams[0].Reason.Contains("workpiece"), "Warning identifies the colliding object");
            Require(!preview.Seams[0].Partial && preview.Seams[0].Coverage > .999f, "Fully reachable collision seam retains complete coverage");
            Require(preview.Seams[0].OrientationStrategy == "CAD bisector; work 0 deg; travel 0 deg; roll 0 deg" && !preview.Seams[0].AutoOriented,
                "Warning fallback reports the actual nominal work, travel and roll angles");
            Require(preview.Seams[0].OrientationAttempts > safeOrientationAttempts,
                "Orientation attempt count includes the warning fallback search");
            Require(!preview.Motions[^1].ArcOn, "Warning trajectory retracts or stops with the torch off");
            Require(preview.ToJson().Contains("collision freedom and complete seam coverage are not guaranteed"), "Warning JSON does not claim collision-free validation");

            var empty = new CollisionScene(Array.Empty<Vector3>(), Array.Empty<RobotCapsule>());
            var partial = Seam(start - home.Basis.X * 8f, start, start + home.Basis.X * .024f, start + home.Basis.X * 8f);
            var partialRequest = Request(true, empty, partial, .2f);
            WeldProgram limited = WeldPlanner.Plan(partialRequest, cancellation.Token);
            Require(limited.WarningCount == 1 && limited.Seams[0].Partial && limited.Motions.Any(m => m.ArcOn), "Reachable interior portion survives unreachable endpoints");
            Require(limited.Seams[0].ProcessedLength >= .023f && limited.Seams[0].Coverage > 0 && limited.Seams[0].Coverage < 1, "Partial warning reports actual processed length and coverage");
            Require(limited.Seams[0].Reason.Contains("Partial seam"), "Partial warning explains incomplete coverage");
            float[] previous = partialRequest.StartAngles;
            foreach (WeldMotion motion in limited.Motions)
            {
                Require(RobotKinematics.Forward(motion.TargetAngles, partialRequest.JointRest, partialRequest.ToolTransform).Origin.DistanceTo(motion.Tcp.Origin) < .0003f,
                    "Warning trajectories contain real reachable IK targets");
                Require(empty.CheckMotion(previous, motion.TargetAngles, out _), "Warning mode preserves joint travel limits");
                previous = motion.TargetAngles;
            }
            var unreachable = Seam(Vector3.One * 8, Vector3.One * 8 + Vector3.Right * .1f);
            WeldProgram impossible = WeldPlanner.Plan(Request(true, empty, unreachable), cancellation.Token);
            Require(impossible.BlockedCount == 1 && impossible.Motions.Count == 0 && impossible.WarningCount == 0, "Fully unreachable seam never emits fabricated motion");
            WeldProgram normal = WeldPlanner.Plan(Request(true, empty, seam), cancellation.Token);
            Require(normal.ReadyCount == 1 && normal.WarningCount == 0 && normal.Seams[0].Coverage > .999f, "Warning mode still prefers a validated full path");
            GD.Print($"PASS: {_checks} warning planner checks");
            GetTree().Quit(0);
        }
        catch (Exception ex) { GD.PrintErr("FAIL: " + ex); GetTree().Quit(1); }
    }
}
