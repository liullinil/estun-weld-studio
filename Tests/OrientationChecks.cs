using Godot;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace EstunStudio;

/// <summary>Actual bent-torch geometry against a plate; changing CAD face signs must not force the torch through it.</summary>
public partial class OrientationChecks : Node
{
    private int _checks;
    private readonly Transform3D[] _rest = RobotKinematics.StandardRestTransforms();

    public override void _Ready()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            CheckSignedFaceNormals();
            CheckObstructedNominalApproach();
            CheckOrientationAfterCollidingStart();
            CheckCancellation();
            GD.Print($"PASS: {_checks} orientation checks in {watch.Elapsed.TotalSeconds:0.00}s");
            GetTree().Quit(0);
        }
        catch (Exception ex) { GD.PrintErr("FAIL: " + ex); GetTree().Quit(1); }
    }

    private void CheckOrientationAfterCollidingStart()
    {
        Transform3D home = RobotKinematics.Forward(RobotController.HomeAngles, _rest, WeldTorch.ToolTransform);
        Transform3D flange = home * WeldTorch.ToolTransform.AffineInverse();
        Vector3 surface = home.Origin - home.Basis.Y * .003f + home.Basis.X * .22f;
        var plateTransform = new Transform3D(home.Basis, surface - home.Basis.Y * .04f);
        Vector3[] plate = new BoxMesh { Size = new Vector3(.30f, .08f, .30f) }.GetFaces().Select(p => plateTransform * p).ToArray();
        Vector3[] fixture = new BoxMesh { Size = Vector3.One * .014f }.GetFaces()
            .Select(p => flange * (p + new Vector3(0, -.07f, 0))).ToArray();
        var collision = new CollisionScene(plate, WeldTorch.CollisionVolumes(), staticTriangles: fixture, toolTip: WeldTorch.ToolTransform.Origin);
        Require(!collision.Check(RobotController.HomeAngles, out _), "Initial torch pose touches the independent fixture");
        var seam = new CadSeam { Id = "inverted seam after colliding start", Points = new[] { surface, surface + home.Basis.X * .024f },
            NormalA = -(home.Basis.Y + home.Basis.Z).Normalized(), NormalB = -(home.Basis.Y - home.Basis.Z).Normalized() };
        var request = new WeldPlanRequest { Seams = new[] { seam }, ToolTransform = WeldTorch.ToolTransform,
            Collision = collision, RetractDistance = .025f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram program = WeldPlanner.Plan(request, cancellation.Token);
        Require(program.WarningCount == 1 && program.ReadyCount == 0 && program.BlockedCount == 0,
            "A colliding entry remains explicitly a warning even when welding can be safe");
        Require(program.Seams[0].HasCollision && program.Seams[0].AutoOriented && program.Seams[0].Coverage > .999f,
            "Initial collision does not prevent automatic orientation from finding a complete safe welding pass");
        float[] previous = request.StartAngles;
        bool arcStarted = false, entryCollided = false;
        foreach (WeldMotion motion in program.Motions)
        {
            bool clear = collision.CheckMotion(previous, motion.TargetAngles, out string reason);
            if (motion.ArcOn) arcStarted = true;
            if (arcStarted)
            {
                Require(clear, "Warning entry does not allow collisions during welding or retraction: " + reason);
                Require(collision.CheckToolPose(motion.Tcp, request.ToolTransform, out _), "Corrected warning weld keeps the torch outside solids");
            }
            else if (!clear) entryCollided = true;
            previous = motion.TargetAngles;
        }
        Require(arcStarted && entryCollided, "The unavoidable contact is confined to the entry before the arc is on");
    }

    private void CheckObstructedNominalApproach()
    {
        Transform3D home = RobotKinematics.Forward(RobotController.HomeAngles, _rest, WeldTorch.ToolTransform);
        Vector3 surface = home.Origin - home.Basis.Y * .003f + home.Basis.X * .22f;
        Vector3[] Box(Vector3 size, Vector3 offset) => new BoxMesh { Size = size }.GetFaces()
            .Select(p => new Transform3D(home.Basis, surface + home.Basis * offset) * p).ToArray();
        // Obstruct the nominal torch neck while leaving a tilted approach and the
        // entire 25 mm retract corridor clear, with the process arc gap unchanged.
        var collision = new CollisionScene(Box(new Vector3(.30f, .08f, .30f), new Vector3(0, -.04f, 0)),
            WeldTorch.CollisionVolumes(), staticTriangles: Box(new Vector3(.12f, .008f, .065f), new Vector3(0, .16f, 0)), toolTip: WeldTorch.ToolTransform.Origin);
        Require(collision.Check(RobotController.HomeAngles, out _), "Offset reference start is clear of the overhead fixture");
        var seam = new CadSeam { Id = "blocked nominal approach", Points = new[] { surface, surface + home.Basis.X * .024f },
            NormalA = (home.Basis.Y + home.Basis.Z).Normalized(), NormalB = (home.Basis.Y - home.Basis.Z).Normalized() };
        foreach ((float roll, float lean) in new[] { (0f, 0f), (90f, 0f), (-90f, 0f), (180f, 0f), (0f, -12f), (0f, 12f) })
        {
            Vector3 approach = home.Basis.Y * Mathf.Cos(Mathf.DegToRad(lean)) + home.Basis.X * Mathf.Sin(Mathf.DegToRad(lean));
            Basis basis = WeldPlanner.TorchBasis(home.Basis.X, approach, roll);
            Require(!collision.CheckToolPose(new Transform3D(basis, surface + basis.Y * .003f), WeldTorch.ToolTransform, out _),
                "Nominal approach cannot be rescued by the original roll/lean alternatives");
        }
        Basis tilted = WeldPlanner.TorchBasis(home.Basis.X, home.Basis.Y.Rotated(home.Basis.X, Mathf.DegToRad(-22.5f)));
        Require(collision.CheckToolPose(new Transform3D(tilted, surface + tilted.Y * .003f), WeldTorch.ToolTransform, out _),
            "Fixture leaves a tilted welding pose clear with the torch body and tip");
        Require(collision.CheckToolPose(new Transform3D(tilted, surface + tilted.Y * .028f), WeldTorch.ToolTransform, out _),
            "Fixture leaves the matching tilted retract position clear");
        var request = new WeldPlanRequest { Seams = new[] { seam }, ToolTransform = WeldTorch.ToolTransform,
            Collision = collision, RetractDistance = .025f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram program = WeldPlanner.Plan(request, cancellation.Token);
        GD.Print($"ORIENTATION overhead fixture: {program.Summary}; {program.Seams[0].OrientationStrategy}; {program.Seams[0].Reason}");
        Require(program.ReadyCount == 1 && program.WarningCount == 0,
            "An automatically tilted approach clears a fixture that blocks every nominal wrist roll");
        Require(program.Seams[0].AutoOriented, "Fixture-driven approach correction is reported");
        CheckProgram(request, program);
    }

    private void CheckSignedFaceNormals()
    {
        Transform3D home = RobotKinematics.Forward(RobotController.HomeAngles, _rest, WeldTorch.ToolTransform);
        Vector3 surface = home.Origin - home.Basis.Y * .003f;
        var plateTransform = new Transform3D(home.Basis, surface - home.Basis.Y * .04f);
        Vector3[] triangles = new BoxMesh { Size = new Vector3(.30f, .08f, .30f) }.GetFaces().Select(p => plateTransform * p).ToArray();
        var collision = new CollisionScene(triangles, WeldTorch.CollisionVolumes(), toolTip: WeldTorch.ToolTransform.Origin);
        Require(collision.Check(RobotController.HomeAngles, out _), "Reference home torch is clear above the workpiece surface");
        Vector3 normalA = (home.Basis.Y + home.Basis.Z).Normalized();
        Vector3 normalB = (home.Basis.Y - home.Basis.Z).Normalized();
        foreach ((string name, float signA, float signB) in new[]
            { ("correct", 1f, 1f), ("both inverted", -1f, -1f), ("first inverted", -1f, 1f), ("second inverted", 1f, -1f) })
        {
            var seam = new CadSeam { Id = name, Points = new[] { surface, surface + home.Basis.X * .024f },
                NormalA = normalA * signA, NormalB = normalB * signB };
            var request = new WeldPlanRequest { Seams = new[] { seam }, ToolTransform = WeldTorch.ToolTransform,
                Collision = collision, RetractDistance = .035f, SampleSpacing = .006f, AllowWarningPaths = true };
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var watch = Stopwatch.StartNew();
            WeldProgram program = WeldPlanner.Plan(request, cancellation.Token);
            GD.Print($"ORIENTATION {name}: {program.Summary}; {program.Seams[0].Reason}; {watch.Elapsed.TotalSeconds:0.00}s");
            Require(program.ReadyCount == 1 && program.WarningCount == 0 && program.BlockedCount == 0,
                $"{name}: a complete collision-free orientation is selected before warning preview");
            Require(program.Seams[0].OrientationAttempts > 0 && !string.IsNullOrWhiteSpace(program.Seams[0].OrientationStrategy),
                $"{name}: program records the evaluated and selected orientation");
            if (name != "correct") Require(program.Seams[0].AutoOriented,
                $"{name}: corrected orientation is identified in the program metadata");
            CheckProgram(request, program);
            Require(seam.NormalA == normalA * signA && seam.NormalB == normalB * signB,
                $"{name}: imported face normals are not changed by planning");
        }

        // Curved face fields come from tessellation rather than one constant seam normal.
        // The field changes gradually while CAD face orientation signs remain inverted.
        Vector3[] points = Enumerable.Range(0, 5).Select(i => surface + home.Basis.X * (.006f * i)).ToArray();
        Vector3[] normalsA = Enumerable.Range(0, 5).Select(i => new Basis(home.Basis.X, .025f * i) * -normalA).ToArray();
        Vector3[] normalsB = Enumerable.Range(0, 5).Select(i => new Basis(home.Basis.X, .025f * i) * -normalB).ToArray();
        var varying = new CadSeam { Id = "varying inverted face fields", Points = points,
            NormalA = -normalA, NormalB = -normalB, NormalsA = normalsA.ToArray(), NormalsB = normalsB.ToArray() };
        var varyingRequest = new WeldPlanRequest { Seams = new[] { varying }, ToolTransform = WeldTorch.ToolTransform,
            Collision = collision, RetractDistance = .035f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var varyingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram varyingProgram = WeldPlanner.Plan(varyingRequest, varyingCancellation.Token);
        Require(varyingProgram.ReadyCount == 1 && varyingProgram.WarningCount == 0,
            "Varying inverted CAD normal fields retain a continuous collision-free orientation");
        CheckProgram(varyingRequest, varyingProgram);
        Require(varying.NormalsA.SequenceEqual(normalsA) && varying.NormalsB.SequenceEqual(normalsB),
            "Per-point CAD face normal fields remain immutable");

        var flippedA = new[] { -normalA, -normalA, normalA, -normalA, -normalA };
        var flippedB = new[] { -normalB, normalB, -normalB, -normalB, -normalB };
        var flipSeam = new CadSeam { Id = "per-sample CAD sign flip", Points = points,
            NormalA = -normalA, NormalB = -normalB, NormalsA = flippedA.ToArray(), NormalsB = flippedB.ToArray() };
        var flipRequest = new WeldPlanRequest { Seams = new[] { flipSeam }, ToolTransform = WeldTorch.ToolTransform,
            Collision = collision, RetractDistance = .035f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var flipCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram flipProgram = WeldPlanner.Plan(flipRequest, flipCancellation.Token);
        Require(flipProgram.ReadyCount == 1 && flipProgram.WarningCount == 0,
            "Isolated near-antipodal CAD normal sign flips do not turn the torch into the plate");
        CheckProgram(flipRequest, flipProgram);
        Require(flipSeam.NormalsA.SequenceEqual(flippedA) && flipSeam.NormalsB.SequenceEqual(flippedB),
            "Unwrapping orientation signs does not modify source CAD fields");

        var opposingSeam = new CadSeam { Id = "opposing sheet normals", Points = new[] { surface, surface + home.Basis.X * .024f },
            NormalA = -home.Basis.Y, NormalB = home.Basis.Y };
        var opposingRequest = new WeldPlanRequest { Seams = new[] { opposingSeam }, ToolTransform = WeldTorch.ToolTransform,
            Collision = collision, RetractDistance = .035f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var opposingCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram opposingProgram = WeldPlanner.Plan(opposingRequest, opposingCancellation.Token);
        Require(opposingProgram.ReadyCount == 1 && opposingProgram.WarningCount == 0,
            "Opposing sheet normals retain a finite free-side approach instead of the first inward normal");
        CheckProgram(opposingRequest, opposingProgram);

        Transform3D placement = new(new Basis(Vector3.Up, .63f) * new Basis(Vector3.Right, -.24f), new Vector3(.2f, .1f, -.3f));
        var transformedSeam = new CadSeam { Id = "placed inverted CAD faces",
            Points = new[] { placement.AffineInverse() * surface, placement.AffineInverse() * (surface + home.Basis.X * .024f) },
            NormalA = placement.Basis.Inverse() * -normalA, NormalB = placement.Basis.Inverse() * -normalB };
        var transformedRequest = new WeldPlanRequest { Seams = new[] { transformedSeam }, PartTransform = placement,
            ToolTransform = WeldTorch.ToolTransform, Collision = collision, RetractDistance = .035f, SampleSpacing = .006f, AllowWarningPaths = true };
        using var transformedCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        WeldProgram transformedProgram = WeldPlanner.Plan(transformedRequest, transformedCancellation.Token);
        Require(transformedProgram.ReadyCount == 1 && transformedProgram.WarningCount == 0,
            "Automatic orientation follows the placed CAD normal frame");
        CheckProgram(transformedRequest, transformedProgram);
    }

    private void CheckProgram(WeldPlanRequest request, WeldProgram program)
    {
        float[] previous = request.StartAngles;
        WeldMotion? priorArc = null;
        foreach (WeldMotion motion in program.Motions)
        {
            Require(request.Collision.CheckMotion(previous, motion.TargetAngles, out string reason),
                "Every emitted weld, approach, transfer and retract remains collision free: " + reason);
            Transform3D actual = RobotKinematics.Forward(motion.TargetAngles, request.JointRest, request.ToolTransform);
            Require(request.Collision.CheckToolPose(actual, request.ToolTransform, out string tipReason),
                "Emitted tool pose is clear and the tip remains outside closed solids: " + tipReason);
            Require(actual.Origin.DistanceTo(motion.Tcp.Origin) < .0003f, "Emitted TCP agrees with forward kinematics within 0.3 mm");
            Require(RobotKinematics.RotationError(motion.Tcp.Basis, actual.Basis).Length() < .0012f,
                "Emitted tool orientation agrees with forward kinematics");
            if (motion.ArcOn)
            {
                Require(Math.Abs(motion.Tcp.Origin.DistanceTo(motion.WeldPoint) - request.ArcGap) < .00001f,
                    "Orientation search preserves the specified arc gap");
                Require(motion.WorldApproach.IsFinite() && Math.Abs(motion.WorldApproach.Length() - 1) < .0001f,
                    "Weld approach is a finite unit vector");
                if (priorArc != null)
                {
                    Require(motion.WorldApproach.Dot(priorArc.WorldApproach) > .98f, "Tool direction is continuous along the straight seam");
                    Require(motion.TargetAngles.Zip(priorArc.TargetAngles, (a, b) => Math.Abs(a - b)).Max() <= 18,
                        "Weld motion preserves the continuous joint branch");
                }
                priorArc = motion;
            }
            previous = motion.TargetAngles;
        }
        Require(priorArc != null && program.Seams[0].Coverage > .999f, "Entire seam is welded");
        Require(!program.Motions[^1].ArcOn, "Retraction leaves the arc off");
    }

    private void CheckCancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool cancelled = false;
        try
        {
            WeldPlanner.Plan(new WeldPlanRequest { Seams = new[] { new CadSeam { Id = "cancel", Points = new[] { Vector3.Zero, Vector3.Right } } } }, cancel.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled, "Cancelled planning cannot publish a partial program");

        using var duringSearch = new CancellationTokenSource();
        int reports = 0; cancelled = false;
        try
        {
            WeldPlanner.Plan(new WeldPlanRequest { Seams = new[] { new CadSeam { Id = "cancel during search",
                Points = new[] { new Vector3(.8f, .5f, 0), new Vector3(.824f, .5f, 0) } } } }, duringSearch.Token,
                (_, _) => { if (++reports >= 2) duringSearch.Cancel(); });
        }
        catch (OperationCanceledException) { cancelled = true; }
        Require(reports >= 2 && cancelled, "An active orientation search responds to cancellation without returning a program");
    }

    private void Require(bool pass, string label)
    { if (!pass) throw new InvalidOperationException(label); _checks++; }
}
