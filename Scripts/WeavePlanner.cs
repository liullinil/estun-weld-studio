using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EstunStudio;

/// <summary>Application-defined sinusoidal weave, explicitly sampled rather than delegated to an undocumented controller waveform.</summary>
public sealed class WeavingOptions
{
    public bool Enabled { get; set; }
    public float AmplitudeMm { get; set; } = 1;
    public float FrequencyHz { get; set; } = 2;
    public float AngleDegrees { get; set; }
    public float FadeMm { get; set; } = 2;

    public void Validate()
    {
        if (!float.IsFinite(AmplitudeMm) || AmplitudeMm < .1f || AmplitudeMm > 10 ||
            !float.IsFinite(FrequencyHz) || FrequencyHz < .1f || FrequencyHz > 5 ||
            !float.IsFinite(AngleDegrees) || AngleDegrees < -180 || AngleDegrees > 180 ||
            !float.IsFinite(FadeMm) || FadeMm < .5f || FadeMm > 20)
            throw new ArgumentException("Weaving requires amplitude 0.1–10 mm, frequency 0.1–5 Hz, angle −180–180°, and endpoint fade 0.5–20 mm.");
    }
}

public static class WeavePlanner
{
    private static readonly float[] JointSpeeds = { 70, 70, 90, 100, 100, 100 };

    public static WeldProgram Apply(WeldProgram source, WeldPlanRequest request, WeavingOptions settings,
        CancellationToken cancellation = default, Action<float, string>? progress = null)
    {
        settings.Validate();
        cancellation.ThrowIfCancellationRequested();
        if (!settings.Enabled) return source;
        var output = new WeldProgram { StartAngles = source.StartAngles, ToolTransform = source.ToolTransform, PartName = source.PartName };
        output.Seams.AddRange(source.Seams);
        for (int start = 0; start < source.Motions.Count;)
        {
            cancellation.ThrowIfCancellationRequested();
            WeldMotion first = source.Motions[start];
            if (!first.ArcOn) { output.Motions.Add(first); start++; continue; }
            int end = start;
            while (end + 1 < source.Motions.Count && source.Motions[end + 1].ArcOn && source.Motions[end + 1].SeamId == first.SeamId) end++;
            progress?.Invoke(start / (float)Math.Max(1, source.Motions.Count), "Validating weave on " + first.SeamId);
            ApplyRun(source, start, end, request, settings, output.Motions, cancellation);
            start = end + 1;
        }
        progress?.Invoke(1, "Woven path validated");
        return output;
    }

    private static void ApplyRun(WeldProgram source, int start, int end, WeldPlanRequest request, WeavingOptions settings,
        List<WeldMotion> output, CancellationToken cancellation)
    {
        int count = end - start + 1;
        var joints = new float[count + 1][];
        var poses = new Transform3D[count + 1];
        var times = new float[count + 1];
        var distances = new float[count + 1];
        joints[0] = start == 0 ? source.StartAngles : source.Motions[start - 1].TargetAngles;
        poses[0] = RobotKinematics.Forward(joints[0], request.JointRest, request.ToolTransform);
        for (int i = 1; i <= count; i++)
        {
            WeldMotion motion = source.Motions[start + i - 1];
            joints[i] = motion.TargetAngles;
            poses[i] = RobotKinematics.Forward(joints[i], request.JointRest, request.ToolTransform);
            times[i] = times[i - 1] + motion.DurationSeconds;
            distances[i] = distances[i - 1] + poses[i - 1].Origin.DistanceTo(poses[i].Origin);
        }
        float duration = times[^1], length = distances[^1];
        if (duration <= 0 || length < .0001f) throw new InvalidOperationException("Cannot weave a stationary welding segment.");
        var tangents = new Vector3[count + 1];
        for (int i = 0; i <= count; i++) tangents[i] = (poses[Math.Min(count, i + 1)].Origin - poses[Math.Max(0, i - 1)].Origin).Normalized();

        Transform3D Target(float time)
        {
            int segment = Array.BinarySearch(times, time);
            segment = segment >= 0 ? Math.Min(count - 1, segment) : Math.Clamp(~segment - 1, 0, count - 1);
            float u = Mathf.Clamp((time - times[segment]) / (times[segment + 1] - times[segment]), 0, 1);
            float[] q = Interpolate(joints[segment], joints[segment + 1], u);
            Transform3D pose = RobotKinematics.Forward(q, request.JointRest, request.ToolTransform);
            Vector3 tangent = tangents[segment].Lerp(tangents[segment + 1], u).Normalized();
            Vector3 lateral = tangent.Cross(pose.Basis.Y).Normalized();
            if (lateral.LengthSquared() < .9f) throw new InvalidOperationException("Weaving direction is undefined for a stationary seam or tool parallel to travel.");
            lateral = lateral.Rotated(tangent, Mathf.DegToRad(settings.AngleDegrees));
            float distance = Mathf.Lerp(distances[segment], distances[segment + 1], u);
            float fade = Mathf.Clamp(Math.Min(distance, length - distance) / (settings.FadeMm * .001f), 0, 1);
            fade = fade * fade * (3 - 2 * fade);
            pose.Origin += lateral * (settings.AmplitudeMm * .001f * Mathf.Sin(Mathf.Tau * settings.FrequencyHz * time) * fade);
            return pose;
        }

        int steps = Math.Max(4, (int)Math.Ceiling(Math.Max(duration * settings.FrequencyHz * 32, length / .0005f)));
        if (steps > 50000) throw new InvalidOperationException("Weaving exceeds the 50,000-sample limit; reduce the frequency or job length.");
        var sampleTimes = Enumerable.Range(1, steps).Select(i => duration * i / steps).Concat(times.Skip(1)).Distinct().OrderBy(t => t).ToList();
        float previousTime = 0;
        float[] previous = joints[0];
        for (int i = 0; i < sampleTimes.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            float time = sampleTimes[i];
            if (time - previousTime < 1e-7f) continue;
            Transform3D desired = Target(time);
            float[] q;
            if (time >= duration) q = joints[^1];
            else
            {
                var solve = RobotKinematics.Solve(desired, previous, request.JointRest, request.ToolTransform, .00001f, .0001f);
                if (!solve.Success) throw Failure(source, start, time / duration, "no continuous inverse kinematics solution");
                q = solve.AnglesDegrees;
            }
            bool accurate = true;
            foreach (float fraction in new[] { .25f, .5f, .75f })
            {
                Transform3D actual = RobotKinematics.Forward(Interpolate(previous, q, fraction), request.JointRest, request.ToolTransform);
                Transform3D ideal = Target(Mathf.Lerp(previousTime, time, fraction));
                if (actual.Origin.DistanceTo(ideal.Origin) > .000025f || RobotKinematics.RotationError(actual.Basis, ideal.Basis).Length() > .001f) { accurate = false; break; }
            }
            if (!accurate)
            {
                if (time - previousTime < .00001f || sampleTimes.Count >= 50000) throw Failure(source, start, time / duration, "sampling tolerance cannot be met");
                sampleTimes.Insert(i, (previousTime + time) * .5f); i--; continue;
            }
            for (int axis = 0; axis < 6; axis++)
                if (Math.Abs(q[axis] - previous[axis]) / (time - previousTime) > JointSpeeds[axis] * 1.001f)
                    throw Failure(source, start, time / duration, "joint speed limit; reduce weave amplitude or frequency");
            if (!request.Collision.CheckMotion(previous, q, out string collision, cancellation)) throw Failure(source, start, time / duration, collision);
            Transform3D tcp = RobotKinematics.Forward(q, request.JointRest, request.ToolTransform);
            output.Add(new WeldMotion { TargetAngles = q, Tcp = tcp, WeldPoint = tcp.Origin - tcp.Basis.Y * request.ArcGap,
                WorldApproach = tcp.Basis.Y, ArcOn = true, SeamId = source.Motions[start].SeamId, DurationSeconds = time - previousTime });
            previous = q; previousTime = time;
        }
    }

    private static InvalidOperationException Failure(WeldProgram source, int start, float t, string reason) =>
        new($"Weaving on {source.Motions[start].SeamId} at {t * 100:0}%: {reason}. No woven program was generated.");
    private static float[] Interpolate(float[] a, float[] b, float t) => Enumerable.Range(0, 6).Select(i => Mathf.Lerp(a[i], b[i], t)).ToArray();
}
