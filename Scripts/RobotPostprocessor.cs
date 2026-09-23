using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace EstunStudio;

public sealed class RobotPostprocessorOptions
{
    public int Tool { get; init; } = 1;
    public int CoordinateSystem { get; init; } = 1;
    public float PositionToleranceMetres { get; init; } = .0005f;
    // The supplied manual does not specify changing-orientation interpolation for movC.
    // Only essentially constant orientation is compressed, for both Cartesian primitives.
    public float OrientationToleranceRadians { get; init; } = .001f;
    public float LinearAcceleration { get; init; } = 1000;
    public float JointAcceleration { get; init; } = 300;
    public int TorchDigitalOutput { get; init; } = 1;
    public bool LinearizeArcs { get; init; }
    public WeavingOptions Weaving { get; init; } = new();
}

public sealed class RobotProgramPrimitive
{
    public string Command { get; init; } = "movJ";
    public int SourceStart { get; init; }
    public int SourceEnd { get; init; }
    public bool ArcOn { get; init; }
    public string SeamId { get; init; } = "";
    public float[] StartJoints { get; init; } = Array.Empty<float>();
    public float[] EndJoints { get; init; } = Array.Empty<float>();
    public float[]? ViaJoints { get; init; }
    public Transform3D StartPose { get; init; }
    public Transform3D EndPose { get; init; }
    public Transform3D? ViaPose { get; init; }
    public float Speed { get; init; }
    public float DurationSeconds { get; init; }
    public WeldMotion[] Samples { get; init; } = Array.Empty<WeldMotion>();
}

public sealed class RobotPostprocessorResult
{
    public string Text { get; init; } = "";
    public string FileName { get; init; } = "ency-hyper-estun.lua";
    public object Metadata { get; init; } = new();
    public RobotProgramPrimitive[] Primitives { get; init; } = Array.Empty<RobotProgramPrimitive>();
    public WeldMotion[] PlaybackMotions { get; init; } = Array.Empty<WeldMotion>();
    public int JointMoves => Primitives.Count(p => p.Command == "movJ");
    public int LinearMoves => Primitives.Count(p => p.Command == "movL");
    public int CircularMoves => Primitives.Count(p => p.Command == "movC");
}

/// <summary>
/// CODROID Lua dialect from commands.zip. Export targets use the documented jp form,
/// avoiding an invented Cartesian Euler convention. Cartesian replacements are fitted to
/// actual FK, independently solved and collision checked; browser playback must use
/// PlaybackMotions. Validation is sampled offline geometry, not a controller certificate.
/// </summary>
public static class RobotPostprocessor
{
    private sealed record Sample(float T, float[] Joints, Transform3D Pose);
    private sealed class Curve
    {
        public required Transform3D Start, End;
        public Transform3D? Via;
        public int ViaIndex = -1;
        public Vector3 Centre, Radial, Tangent, Normal;
        public float Radius, Sweep, Length, ViaT;
        public bool Circular => Via.HasValue;
        public Transform3D At(float t)
        {
            var pose = Start.InterpolateWith(End, t);
            if (Circular)
            {
                pose.Origin = Centre + Radius * (Radial * Mathf.Cos(Sweep * t) + Tangent * Mathf.Sin(Sweep * t));
                // Orientation variation is bounded by 0.057 degrees by default.
                pose.Basis = t <= ViaT ? Start.Basis.Slerp(Via!.Value.Basis, t / ViaT)
                    : Via!.Value.Basis.Slerp(End.Basis, (t - ViaT) / (1 - ViaT));
            }
            return pose;
        }
        public float Parameter(Vector3 p) => Circular
            ? Mathf.Atan2((p - Centre).Dot(Tangent), (p - Centre).Dot(Radial)) / Sweep
            : (p - Start.Origin).Dot(End.Origin - Start.Origin) / (Length * Length);
    }

    public static RobotPostprocessorResult Export(WeldProgram program, WeldPlanRequest request,
        CancellationToken cancellation = default, Action<float, string>? progress = null, RobotPostprocessorOptions? options = null)
    {
        options ??= new RobotPostprocessorOptions();
        options.Weaving.Validate();
        if (options.Tool < 0 || options.CoordinateSystem < 0 || options.TorchDigitalOutput < 0 || options.TorchDigitalOutput > 65535 || !Positive(options.PositionToleranceMetres) ||
            !Positive(options.OrientationToleranceRadians) || !Positive(options.LinearAcceleration) || !Positive(options.JointAcceleration))
            throw new ArgumentException("Invalid controller postprocessor options.");
        if (program.StartAngles.Length != 6 || request.JointRest.Length != 6 || !program.StartAngles.SequenceEqual(request.StartAngles) ||
            program.ToolTransform != request.ToolTransform)
            throw new ArgumentException("Postprocessor requires the original planning kinematics and starting pose.");
        cancellation.ThrowIfCancellationRequested();
        Transform3D Fk(float[] q) => RobotKinematics.Forward(q, request.JointRest, request.ToolTransform);
        var poses = new Transform3D[program.Motions.Count + 1];
        poses[0] = Fk(program.StartAngles);
        for (int i = 0; i < program.Motions.Count; i++)
        {
            WeldMotion m = program.Motions[i];
            if (m.TargetAngles.Length != 6 || m.TargetAngles.Any(q => !float.IsFinite(q)) || !Positive(m.DurationSeconds))
                throw new ArgumentException("Invalid source motion.");
            poses[i + 1] = Fk(m.TargetAngles);
        }
        var primitives = new List<RobotProgramPrimitive>();
        var playback = new List<WeldMotion>();
        // Warning paths must preserve exactly the joint interpolation reviewed in the simulator.
        // A fitted Cartesian replacement would be a different, unreviewed collision trajectory.
        var warningSeams = program.Seams.Where(s => s.State == WeldSeamState.Warning).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = 0; i < program.Motions.Count;)
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Invoke(i / (float)Math.Max(1, program.Motions.Count), "Optimizing and validating controller path");
            WeldMotion source = program.Motions[i];
            bool preserveWarningPath = warningSeams.Contains(source.SeamId);
            int runEnd = i;
            // Retain the exact original joint transfer geometry. Compress only welding runs.
            if (source.ArcOn && !options.Weaving.Enabled && !preserveWarningPath)
                while (runEnd + 1 < program.Motions.Count && program.Motions[runEnd + 1].ArcOn && program.Motions[runEnd + 1].SeamId == source.SeamId) runEnd++;
            RobotProgramPrimitive? primitive = null;
            for (int end = runEnd; source.ArcOn && !preserveWarningPath && end >= i; end = end == i ? i - 1 : i + (end - i - 1) / 2)
            {
                cancellation.ThrowIfCancellationRequested();
                Curve? curve = Fit(program, poses, i, end, false, request, options, cancellation, out List<Sample> reference);
                if (curve == null && end > i && !options.LinearizeArcs && !options.Weaving.Enabled)
                    curve = Fit(program, poses, i, end, true, request, options, cancellation, out reference);
                if (curve != null)
                    primitive = Validate(program, i, end, curve, reference, request, options, cancellation);
                if (primitive != null) break;
            }
            if (primitive == null)
            {
                float[] from = i == 0 ? program.StartAngles : program.Motions[i - 1].TargetAngles;
                primitive = new RobotProgramPrimitive
                {
                    SourceStart = i, SourceEnd = i, ArcOn = source.ArcOn, SeamId = source.SeamId,
                    StartJoints = (float[])from.Clone(), EndJoints = (float[])source.TargetAngles.Clone(),
                    StartPose = poses[i], EndPose = poses[i + 1], DurationSeconds = source.DurationSeconds,
                    Speed = Math.Max(.001f, MaximumChange(from, source.TargetAngles) / source.DurationSeconds), Samples = new[] { source }
                };
            }
            primitives.Add(primitive); playback.AddRange(primitive.Samples); i = primitive.SourceEnd + 1;
        }
        string text = Format(program, primitives, options);
        var metadata = new
        {
            language = "CODROID Lua (user-supplied commands.zip)",
            sourceDocuments = new[] { "commands/00-lua-basics.md", "commands/01-move.md#movj", "commands/01-move.md#movl", "commands/01-move.md#movc", "commands/02-move-params.md#setnoblender", "commands/03-move-compute.md#getjoint", "commands/08-io.md#setdo" },
            targetRepresentation = "jp: six controller joint angles in degrees; no Cartesian Euler conversion",
            modelFrame = "Robot base, right handed, Y up, metres. CAD-to-model (x,y,z)=(CAD y,CAD z,CAD x).",
            tool = options.Tool, coordinateSystem = options.CoordinateSystem, torchDigitalOutput = options.TorchDigitalOutput,
            linearizeArcs = options.LinearizeArcs, weaving = options.Weaving,
            weavingImplementation = options.Weaving.Enabled ? "Explicit sinusoidal path sampled in native planner and exported as movL/movJ with IK and collision checking; reported warning collisions remain in the output. Amplitude is one-sided millimetres; frequency is nominal Hz; angle rotates the tool/travel lateral plane; endpoint fade is smoothstep in millimetres. Controller movLW/movCW waveform/frame are unspecified in the supplied manual and are not inferred." : "Disabled",
            positionToleranceMm = options.PositionToleranceMetres * 1000,
            orientationToleranceDegrees = Mathf.RadToDeg(options.OrientationToleranceRadians),
            validation = "Original joint transfers retained. Cartesian primitives fitted against densely sampled source FK and checked for collision. Warning seams retain the original joint trajectory, including explicitly reported collisions and partial coverage; they are not collision-free. Sampled offline geometry only.",
            warningSeams = program.Seams.Where(s => s.State == WeldSeamState.Warning).Select(s => new { id = s.Id, reason = s.Reason, hasCollision = s.HasCollision, partial = s.Partial, coverage = s.Coverage }),
            orientation = "Only approximately constant tool orientation is compressed; the supplied manual does not specify general movC orientation interpolation.",
            approachSelection = program.Seams.Select(s => new { id = s.Id, strategy = s.OrientationStrategy, attempts = s.OrientationAttempts, adjusted = s.AutoOriented }),
            controllerRequirements = "Controller must implement the supplied CODROID Lua dialect, ESTUN S20-180 Pro model and matching joint zeros/directions, tool, base/workpiece calibration. No controller calibration is inferred or overwritten.",
            timing = "Nominal feed from planned duration; controller acceleration and exact-stop timing may differ from simulation.",
            initialJoints = program.StartAngles, startGuardToleranceDegrees = .2f,
            sourceMotions = program.Motions.Count, controllerMotions = primitives.Count, playbackSamples = playback.Count,
            jointMoves = primitives.Count(p => p.Command == "movJ"), linearMoves = primitives.Count(p => p.Command == "movL"), circularMoves = primitives.Count(p => p.Command == "movC")
        };
        progress?.Invoke(1, "Controller program ready");
        return new RobotPostprocessorResult { Text = text, Metadata = metadata, Primitives = primitives.ToArray(), PlaybackMotions = playback.ToArray() };
    }

    private static Curve? Fit(WeldProgram program, Transform3D[] poses, int start, int end, bool circular,
        WeldPlanRequest request, RobotPostprocessorOptions options, CancellationToken cancellation, out List<Sample> reference)
    {
        reference = new List<Sample>();
        var curve = new Curve { Start = poses[start], End = poses[end + 1] };
        Vector3 chord = curve.End.Origin - curve.Start.Origin;
        curve.Length = chord.Length();
        if (curve.Length < .00001f) return null;
        if (circular)
        {
            int viaIndex = start + (end - start) / 2;
            Vector3 a = poses[viaIndex + 1].Origin - curve.Start.Origin, b = chord;
            Vector3 cross = a.Cross(b);
            if (cross.LengthSquared() < 1e-14f) return null;
            curve.Centre = curve.Start.Origin + (b.Cross(cross) * a.LengthSquared() + cross.Cross(a) * b.LengthSquared()) / (2 * cross.LengthSquared());
            curve.Radius = curve.Start.Origin.DistanceTo(curve.Centre);
            if (!float.IsFinite(curve.Radius) || curve.Radius < .001f) return null;
            curve.Normal = cross.Normalized();
            curve.Radial = (curve.Start.Origin - curve.Centre).Normalized();
            curve.Tangent = curve.Normal.Cross(curve.Radial);
            curve.Sweep = Mathf.Atan2((curve.End.Origin - curve.Centre).Dot(curve.Tangent), (curve.End.Origin - curve.Centre).Dot(curve.Radial));
            if (curve.Sweep <= .001f || curve.Sweep >= Mathf.Pi - .01f) return null;
            curve.Length = curve.Sweep * curve.Radius;
            // Do not emit numerically unstable arcs indistinguishable from straight lines.
            if (curve.Radius * (1 - Mathf.Cos(curve.Sweep * .5f)) < options.PositionToleranceMetres * 2) return null;
            curve.Via = poses[viaIndex + 1]; curve.ViaIndex = viaIndex;
            curve.ViaT = curve.Parameter(curve.Via.Value.Origin);
            if (curve.ViaT <= .01f || curve.ViaT >= .99f) return null;
        }
        float previousT = -1;
        for (int index = start; index <= end; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            float[] from = index == 0 ? program.StartAngles : program.Motions[index - 1].TargetAngles;
            float[] to = program.Motions[index].TargetAngles;
            int steps = Math.Max(4, (int)Math.Ceiling(Math.Max(MaximumChange(from, to) / .1f, poses[index].Origin.DistanceTo(poses[index + 1].Origin) / .002f)));
            for (int step = index == start ? 0 : 1; step <= steps; step++)
            {
                if ((step & 31) == 0) cancellation.ThrowIfCancellationRequested();
                float[] q = Interpolate(from, to, step / (float)steps);
                Transform3D pose = RobotKinematics.Forward(q, request.JointRest, request.ToolTransform);
                if (RobotKinematics.RotationError(pose.Basis, curve.Start.Basis).Length() > options.OrientationToleranceRadians) return null;
                float t = curve.Parameter(pose.Origin);
                if (!float.IsFinite(t) || t < -.0001f || t > 1.0001f || t < previousT - .0001f) return null;
                t = Mathf.Clamp(t, 0, 1);
                Transform3D expected = curve.At(t);
                if (pose.Origin.DistanceTo(expected.Origin) > options.PositionToleranceMetres ||
                    RobotKinematics.RotationError(pose.Basis, expected.Basis).Length() > options.OrientationToleranceRadians) return null;
                reference.Add(new Sample(t, q, pose)); previousT = t;
            }
        }
        return curve;
    }

    private static RobotProgramPrimitive? Validate(WeldProgram program, int start, int end, Curve curve, List<Sample> reference,
        WeldPlanRequest request, RobotPostprocessorOptions options, CancellationToken cancellation)
    {
        float[] first = start == 0 ? program.StartAngles : program.Motions[start - 1].TargetAngles;
        float[] previous = first;
        var samples = new List<WeldMotion>();
        float duration = 0; for (int i = start; i <= end; i++) duration += program.Motions[i].DurationSeconds;
        int steps = Math.Max(4, (int)Math.Ceiling(curve.Length / .004f));
        // Include the exact documented via point as a playback sample.
        var times = Enumerable.Range(1, steps).Select(i => i / (float)steps).ToList();
        if (curve.Circular) { times.Add(curve.ViaT); times.Sort(); }
        float previousT = 0;
        int referenceIndex = 0;
        for (int index = 0; index < times.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            float t = times[index]; if (t - previousT < 1e-6f) continue;
            Transform3D target = curve.At(t);
            float[] q;
            if (t >= 1) q = program.Motions[end].TargetAngles;
            else if (curve.Circular && Math.Abs(t - curve.ViaT) < 1e-6f) q = program.Motions[curve.ViaIndex].TargetAngles;
            else
            {
                var solve = RobotKinematics.Solve(target, previous, request.JointRest, request.ToolTransform, .000015f, .0001f);
                if (!solve.Success) return null;
                q = solve.AnglesDegrees;
            }
            // Pin the same IK branch, independently of the Cartesian closeness check.
            while (referenceIndex + 1 < reference.Count && reference[referenceIndex + 1].T < t) referenceIndex++;
            Sample a = reference[referenceIndex], b = reference[Math.Min(reference.Count - 1, referenceIndex + 1)];
            float[] expectedJoints = Interpolate(a.Joints, b.Joints, b.T - a.T > 1e-7f ? Mathf.Clamp((t - a.T) / (b.T - a.T), 0, 1) : 1);
            if (MaximumChange(q, expectedJoints) > 2) return null;
            bool accurate = true;
            foreach (float fraction in new[] { .25f, .5f, .75f, 1f })
            {
                Transform3D actual = RobotKinematics.Forward(Interpolate(previous, q, fraction), request.JointRest, request.ToolTransform);
                Transform3D ideal = curve.At(Mathf.Lerp(previousT, t, fraction));
                if (actual.Origin.DistanceTo(ideal.Origin) > Math.Min(.0001f, options.PositionToleranceMetres * .25f) ||
                    RobotKinematics.RotationError(actual.Basis, ideal.Basis).Length() > options.OrientationToleranceRadians) { accurate = false; break; }
            }
            if (!accurate)
            {
                if (t - previousT < 1e-5f || times.Count > 8192) return null;
                times.Insert(index, (previousT + t) * .5f); index--; continue;
            }
            if (!request.Collision.CheckMotion(previous, q, out _, cancellation)) return null;
            Transform3D pose = RobotKinematics.Forward(q, request.JointRest, request.ToolTransform);
            samples.Add(new WeldMotion
            {
                TargetAngles = (float[])q.Clone(), Tcp = pose, WeldPoint = pose.Origin - pose.Basis.Y * request.ArcGap,
                WorldApproach = pose.Basis.Y, ArcOn = true, SeamId = program.Motions[start].SeamId,
                DurationSeconds = duration * (t - previousT)
            });
            previous = q; previousT = t;
        }
        return new RobotProgramPrimitive
        {
            Command = curve.Circular ? "movC" : "movL", SourceStart = start, SourceEnd = end,
            ArcOn = true, SeamId = program.Motions[start].SeamId, StartJoints = (float[])first.Clone(),
            EndJoints = (float[])program.Motions[end].TargetAngles.Clone(),
            ViaJoints = curve.Circular ? (float[])program.Motions[curve.ViaIndex].TargetAngles.Clone() : null,
            StartPose = curve.Start, EndPose = curve.End, ViaPose = curve.Via,
            DurationSeconds = duration, Speed = curve.Length * 1000 / duration, Samples = samples.ToArray()
        };
    }

    private static string Format(WeldProgram program, IReadOnlyList<RobotProgramPrimitive> primitives, RobotPostprocessorOptions options)
    {
        var text = new StringBuilder();
        text.AppendLine("-- ENCY HYPER - ESTUN / CODROID Lua (commands.zip)");
        text.AppendLine("-- Model: ESTUN S20-180 Pro; jp target angles are degrees.");
        text.AppendLine("-- Requires matching controller model, joint zeros/directions and calibrated cell/tool.");
        text.AppendLine($"-- Uses existing tool={options.Tool}, coor={options.CoordinateSystem}; does not overwrite calibration.");
        text.AppendLine("-- Simulator frame: robot base, right handed, Y up, metres; jp avoids Euler assumptions.");
        text.AppendLine($"-- Cartesian fit tolerance: {Number(options.PositionToleranceMetres * 1000)} mm; orientation: {Number(Mathf.RadToDeg(options.OrientationToleranceRadians))} deg.");
        text.AppendLine("-- L/C: independently sampled IK/collision validation. Warning/source paths retain movJ.");
        if (program.WarningCount > 0)
            text.AppendLine($"-- WARNING: {program.WarningCount} seam(s) contain reported collisions or only partial coverage. See per-seam comments.");
        text.AppendLine("-- Offline sampled validation; controller dynamics and cycle time require commissioning.");
        if (options.Weaving.Enabled)
            text.AppendLine($"-- Explicit sinusoidal weave: amplitude={Number(options.Weaving.AmplitudeMm)} mm, frequency={Number(options.Weaving.FrequencyHz)} Hz, angle={Number(options.Weaving.AngleDegrees)} deg, fade={Number(options.Weaving.FadeMm)} mm. Native IK/collision checked.");
        text.AppendLine($"setDO({options.TorchDigitalOutput},0)");
        text.Append("local expectedStart = {").Append(string.Join(",", program.StartAngles.Select(Number))).AppendLine("}");
        text.AppendLine("local actualStart = getJoint()");
        text.AppendLine("for axis=1,6 do");
        text.AppendLine("  if math.abs(actualStart.jp[axis]-expectedStart[axis]) > 0.2 then");
        text.AppendLine("    print(\"Start joints differ from the calculated program; reposition and regenerate\")");
        text.AppendLine("    stopProject()");
        text.AppendLine("    return");
        text.AppendLine("  end");
        text.AppendLine("end");
        text.AppendLine("setNoBlender()");
        bool torch = false;
        string seam = "";
        foreach (RobotProgramPrimitive p in primitives)
        {
            if (p.SeamId != seam)
            {
                seam = p.SeamId; text.Append("-- Seam: ").AppendLine(seam.Replace('\r', ' ').Replace('\n', ' '));
                PlannedSeam? planned = program.Seams.FirstOrDefault(s => s.Id == seam);
                if (planned?.AutoOriented == true)
                    text.Append("-- Automatic tool orientation: ").AppendLine(planned.OrientationStrategy.Replace('\r', ' ').Replace('\n', ' '));
                if (planned?.State == WeldSeamState.Warning)
                {
                    text.Append("-- WARNING: ").AppendLine(planned.Reason.Replace('\r', ' ').Replace('\n', ' '));
                    if (planned.Partial) text.AppendLine($"-- Partial seam: {Number(planned.Coverage * 100)}% processed; unavailable remainder is omitted.");
                    if (planned.HasCollision) text.AppendLine("-- Collision path retained for review; this segment is not collision-free.");
                }
            }
            if (p.ArcOn != torch) { torch = p.ArcOn; text.AppendLine($"setDO({options.TorchDigitalOutput},{(torch ? 1 : 0)})"); }
            text.Append(p.Command).Append('(');
            if (p.ViaJoints != null) text.Append(JointTarget(p.ViaJoints)).Append(',');
            text.Append(JointTarget(p.EndJoints)).Append(",{v=").Append(Number(p.Speed))
                .Append(",a=").Append(Number(p.Command == "movJ" ? options.JointAcceleration : options.LinearAcceleration))
                .Append(",b=0,rb=0,coor=").Append(options.CoordinateSystem).Append(",tool=").Append(options.Tool).AppendLine("})");
        }
        text.AppendLine($"setDO({options.TorchDigitalOutput},0)");
        return text.ToString();
    }

    private static string JointTarget(float[] q) => "{jp={" + string.Join(",", q.Select(Number)) + "}}";
    private static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static bool Positive(float value) => float.IsFinite(value) && value > 0;
    private static float MaximumChange(float[] a, float[] b) { float result = 0; for (int i = 0; i < 6; i++) result = Math.Max(result, Math.Abs(a[i] - b[i])); return result; }
    private static float[] Interpolate(float[] a, float[] b, float t) { var result = new float[6]; for (int i = 0; i < 6; i++) result[i] = Mathf.Lerp(a[i], b[i], t); return result; }
}
