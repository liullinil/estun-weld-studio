using Godot;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>
/// Optional headless HTTP adapter around the unchanged desktop kinematics and planner.
/// Start res://Tests/WebBridge.tscn. A trusted web gateway supplies the original CAD
/// worker document. This scene is never instantiated by the desktop application.
/// </summary>
public partial class WebBridge : Node
{
    private const int MaximumBodyBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly Dictionary<string, CachedCollisionScene> _collisionScenes = new();
    private readonly object _collisionScenesGate = new();
    private readonly SemaphoreSlim _httpSlots = new(4);
    private HttpListener? _listener;
    private RobotCapsule[] _capsules = Array.Empty<RobotCapsule>();
    private Dictionary<int, Vector3[]> _robotTriangles = new();
    private Vector3[] _fixtures = Array.Empty<Vector3>();
    private int _activePlan;

    private sealed class CachedCollisionScene
    {
        public required CollisionScene Scene;
        public DateTime LastUsedUtc = DateTime.UtcNow;
    }

    private sealed class Job
    {
        public readonly object Gate = new();
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly DateTime CreatedUtc = DateTime.UtcNow;
        public required CancellationTokenSource Cancellation;
        public string Status = "running", Message = "Building collision scene", Error = "";
        public float Progress;
        public object? Result;
        public WeldProgram? Program;
        public WeldPlanRequest? Request;
        public WeavingOptions Weaving = new();
        public object Snapshot()
        {
            lock (Gate) return new { jobId = Id, status = Status, progress = Progress, message = Message, createdUtc = CreatedUtc, result = Result, error = Error };
        }
    }

    public override async void _Ready()
    {
        try
        {
            // CylinderMesh is created on the scene thread; planning thereafter is pure math.
            _fixtures = new CylinderMesh { TopRadius = .70f, BottomRadius = .70f, Height = .201f, RadialSegments = 64 }
                .GetFaces().Select(p => p + new Vector3(0, -.1095f, 0)).ToArray();
            string meshPath = ProjectSettings.GlobalizePath(RobotModel.MeshPath);
            _capsules = await Task.Run(() =>
            {
                _robotTriangles = ReadSource(meshPath);
                return CollisionScene.CreateRobotCapsules(_robotTriangles).Concat(WeldTorch.CollisionVolumes()).ToArray();
            });
            string bind = System.Environment.GetEnvironmentVariable("ESTUN_BRIDGE_BIND") ?? "127.0.0.1";
            if (bind == "0.0.0.0") bind = "+";
            if (bind != "+" && bind != "localhost" && !IPAddress.TryParse(bind, out _)) throw new ArgumentException("Invalid ESTUN_BRIDGE_BIND host.");
            int port = int.TryParse(System.Environment.GetEnvironmentVariable("ESTUN_BRIDGE_PORT"), out int configuredPort) ? configuredPort : 18741;
            if (port < 1024 || port > 65535) throw new ArgumentException("Invalid ESTUN_BRIDGE_PORT.");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{bind}:{port}/");
            _listener.Start();
            GD.Print($"ESTUN_WEB_BRIDGE_READY port={port} capsules={_capsules.Length}");
            _ = AcceptAsync();
        }
        catch (Exception exception) { GD.PrintErr("Web bridge startup failed: " + exception); GetTree().Quit(1); }
    }

    public override void _ExitTree()
    {
        _shutdown.Cancel();
        _listener?.Close();
        foreach (Job job in _jobs.Values) { lock (job.Gate) { if (job.Status == "running") job.Cancellation.Cancel(); } }
    }

    private async Task AcceptAsync()
    {
        while (!_shutdown.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_shutdown.IsCancellationRequested || _listener is not { IsListening: true }) { return; }
            _ = Task.Run(async () =>
            {
                if (!await _httpSlots.WaitAsync(0)) { await Reply(context, 429, new { error = "Too many concurrent requests." }); return; }
                try { await DispatchAsync(context); }
                catch (Exception exception)
                {
                    int status = exception is InvalidDataException or JsonException or ArgumentException ? 400 : exception is OperationCanceledException ? 408 : 500;
                    try { await Reply(context, status, new { error = status == 500 ? "Native bridge request failed." : exception.Message }); } catch (Exception) { }
                    if (status == 500) GD.PrintErr(exception);
                }
                finally { _httpSlots.Release(); context.Response.Close(); }
            });
        }
    }

    private async Task DispatchAsync(HttpListenerContext context)
    {
        PruneJobs();
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string method = context.Request.HttpMethod;
        if (method == "GET" && path == "/health")
        {
            await Reply(context, 200, new
            {
                status = "ok", engine = "Godot 4.5.1 .NET / original ESTUN desktop planner", version = 1,
                busy = Volatile.Read(ref _activePlan) != 0, capsuleCount = _capsules.Length,
                maximumBodyBytes = MaximumBodyBytes, coordinates = "robot base; right handed; Y up; metres; degrees",
                homeAngles = RobotController.HomeAngles, jointMinimum = RobotController.JointMinimum, jointMaximum = RobotController.JointMaximum,
                jointRest = RobotKinematics.StandardRestTransforms().Select(Pose), tool = Pose(WeldTorch.ToolTransform),
                collision = new { margin = .002, maximumSummedJointStepDegrees = .032, floorY = -.22, nativeRobotAndTorch = true, nativePlinth = true }
            });
            return;
        }
        if (path.StartsWith("/jobs/", StringComparison.Ordinal) && path.EndsWith("/export", StringComparison.Ordinal) && method == "POST")
        {
            string id = path[6..^7];
            if (!_jobs.TryGetValue(id, out Job? source) || source.Status != "complete" || source.Program == null || source.Request == null)
            { await Reply(context, 404, new { error = "Generate a program before exporting; the source job is unavailable or expired." }); return; }
            ExportRequest input = await ReadRequest<ExportRequest>(context.Request);
            if (input.TorchDigitalOutput < 0 || input.TorchDigitalOutput > 65535) throw new InvalidDataException("Torch digital output must be an integer from 0 to 65535.");
            if (Interlocked.CompareExchange(ref _activePlan, 1, 0) != 0)
            { await Reply(context, 429, new { error = "The native planner is busy. Wait for the current job or cancel it." }); return; }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(10));
            var job = new Job { Cancellation = cancellation, Message = "Preparing export", Program = source.Program, Request = source.Request, Weaving = source.Weaving };
            _jobs[job.Id] = job;
            _ = Task.Run(() => RunExport(job, input));
            await Reply(context, 202, new { jobId = job.Id, status = "running", poll = "/jobs/" + job.Id });
            return;
        }
        if (path.StartsWith("/jobs/", StringComparison.Ordinal) && (method == "GET" || method == "DELETE"))
        {
            string id = path[6..];
            if (!_jobs.TryGetValue(id, out Job? job)) { await Reply(context, 404, new { error = "Unknown or expired job." }); return; }
            if (method == "DELETE") { lock (job.Gate) { if (job.Status == "running") { job.Message = "Cancellation requested"; job.Cancellation.Cancel(); } } }
            await Reply(context, 200, job.Snapshot());
            return;
        }
        if (path == "/plan" && method == "POST")
        {
            if (Interlocked.CompareExchange(ref _activePlan, 1, 0) != 0)
            { await Reply(context, 429, new { error = "The native planner is busy. Wait for the current job or cancel it." }); return; }
            bool scheduled = false;
            try
            {
                BridgeRequest input = await ReadRequest<BridgeRequest>(context.Request);
                Prepared prepared = Prepare(input, requireSelectedSeam: true);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                cancellation.CancelAfter(TimeSpan.FromMinutes(10));
                var job = new Job { Cancellation = cancellation };
                _jobs[job.Id] = job;
                _ = Task.Run(() => RunPlan(job, prepared));
                scheduled = true;
                await Reply(context, 202, new { jobId = job.Id, status = "running", poll = "/jobs/" + job.Id });
            }
            finally { if (!scheduled) Interlocked.Exchange(ref _activePlan, 0); }
            return;
        }
        if (path == "/check" && method == "POST")
        {
            BridgeRequest input = await ReadRequest<BridgeRequest>(context.Request);
            float[] angles = input.StartAngles ?? (float[])RobotController.HomeAngles.Clone();
            if (angles.Length != 6 || angles.Any(a => !float.IsFinite(a))) throw new InvalidDataException("startAngles requires six finite joint angles in degrees.");
            string sceneId = input.SceneId ?? "";
            CollisionScene? collision = null;
            if (sceneId.Length > 0)
            {
                if (input.Document != null || input.PartTransform != null) throw new InvalidDataException("To update a collision scene, submit the document and placement without sceneId.");
                lock (_collisionScenesGate)
                {
                    PruneCollisionScenes();
                    if (_collisionScenes.TryGetValue(sceneId, out CachedCollisionScene? cached))
                    { cached.LastUsedUtc = DateTime.UtcNow; collision = cached.Scene; }
                }
                if (collision == null)
                { await Reply(context, 404, new { error = "Collision scene expired; submit the document and placement again.", code = "collision_scene_expired" }); return; }
            }
            else
            {
                collision = input.Document == null ? new CollisionScene(Array.Empty<Vector3>(), _capsules, staticTriangles: _fixtures, robotTriangles: _robotTriangles) : MakeCollision(Prepare(input));
                sceneId = Guid.NewGuid().ToString("N");
                lock (_collisionScenesGate)
                {
                    PruneCollisionScenes();
                    while (_collisionScenes.Count >= 8) _collisionScenes.Remove(_collisionScenes.MinBy(p => p.Value.LastUsedUtc).Key);
                    _collisionScenes[sceneId] = new CachedCollisionScene { Scene = collision };
                }
            }
            bool clear = collision.CheckDetailed(angles, out string reason, out CollisionContact[] contacts);
            await Reply(context, 200, new { sceneId, clear, reason, links = contacts.SelectMany(c => c.Links).Distinct().OrderBy(i => i).ToArray(),
                collisions = contacts.Select(c => new { kind = c.Kind, reason = c.Reason, links = c.Links }), angles,
                tcp = Pose(RobotKinematics.Forward(angles, RobotKinematics.StandardRestTransforms(), WeldTorch.ToolTransform)),
                validation = "Native pose collision check with adjacent links excluded. Visualization does not disable planner collision checks." });
            return;
        }
        await Reply(context, 404, new { error = "Unknown bridge route." });
    }

    private void RunPlan(Job job, Prepared input)
    {
        try
        {
            job.Cancellation.Token.ThrowIfCancellationRequested();
            CollisionScene collision = MakeCollision(input);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var request = new WeldPlanRequest
            {
                Seams = input.Seams, PartTransform = input.PartTransform, StartAngles = input.StartAngles,
                ToolTransform = WeldTorch.ToolTransform, Collision = collision, PartName = input.Name, AllowWarningPaths = true
            };
            WeldProgram program = WeldPlanner.Plan(request, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = progress * .7f; job.Message = message; } });
            double planningSeconds = watch.Elapsed.TotalSeconds;
            program = WeavePlanner.Apply(program, request, input.Weaving, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = .7f + progress * .15f; job.Message = message; } });
            double weavingSeconds = watch.Elapsed.TotalSeconds - planningSeconds;
            RobotPostprocessorResult controller = RobotPostprocessor.Export(program, request, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = .85f + progress * .1f; job.Message = message; } },
                new RobotPostprocessorOptions { Weaving = input.Weaving, PositionToleranceMetres = input.Weaving.Enabled ? .000025f : .0005f });
            CollisionFrame[][] frames = BuildCollisionTimeline(program, controller.PlaybackMotions, request, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = .95f + progress * .05f; job.Message = message; } });
            object result = MakeResult(program, controller, frames, planningSeconds, weavingSeconds, watch.Elapsed.TotalSeconds, 1, out string summary);
            lock (job.Gate) { job.Program = program; job.Request = request; job.Weaving = input.Weaving; job.Result = result; job.Progress = 1; job.Message = summary; job.Status = "complete"; }
        }
        catch (OperationCanceledException) { lock (job.Gate) { job.Status = "cancelled"; job.Message = "Planning cancelled or exceeded the 10-minute time limit."; } }
        catch (Exception exception) { lock (job.Gate) { job.Status = "failed"; job.Error = exception.Message; job.Message = "Planning failed"; } GD.PrintErr(exception); }
        finally { Interlocked.Exchange(ref _activePlan, 0); }
    }

    private void RunExport(Job job, ExportRequest input)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            RobotPostprocessorResult controller = RobotPostprocessor.Export(job.Program!, job.Request!, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = progress * .9f; job.Message = message; } },
                new RobotPostprocessorOptions { LinearizeArcs = input.LinearizeArcs, TorchDigitalOutput = input.TorchDigitalOutput,
                    Weaving = job.Weaving, PositionToleranceMetres = job.Weaving.Enabled ? .000025f : .0005f });
            CollisionFrame[][] frames = BuildCollisionTimeline(job.Program!, controller.PlaybackMotions, job.Request!, job.Cancellation.Token,
                (progress, message) => { lock (job.Gate) { job.Progress = .9f + progress * .1f; job.Message = message; } });
            object result = MakeResult(job.Program!, controller, frames, 0, 0, watch.Elapsed.TotalSeconds, input.TorchDigitalOutput, out string summary);
            lock (job.Gate) { job.Result = result; job.Progress = 1; job.Message = summary; job.Status = "complete"; }
        }
        catch (OperationCanceledException) { lock (job.Gate) { job.Status = "cancelled"; job.Message = "Export cancelled or exceeded the 10-minute time limit."; } }
        catch (Exception exception) { lock (job.Gate) { job.Status = "failed"; job.Error = exception.Message; job.Message = "Export failed"; } GD.PrintErr(exception); }
        finally { Interlocked.Exchange(ref _activePlan, 0); }
    }

    private static object MakeResult(WeldProgram program, RobotPostprocessorResult controller, CollisionFrame[][] frames, double planningSeconds,
        double weavingSeconds, double totalSeconds, int torchDigitalOutput, out string summary)
    {
        var playback = new WeldProgram { StartAngles = program.StartAngles, ToolTransform = program.ToolTransform, PartName = program.PartName };
        playback.Seams.AddRange(program.Seams); playback.Motions.AddRange(controller.PlaybackMotions);
        JsonNode document = JsonNode.Parse(playback.ToJson())!;
        JsonArray moves = document["moves"]!.AsArray();
        for (int i = 0; i < moves.Count; i++) moves[i]!["collisionFrames"] = JsonSerializer.SerializeToNode(frames[i], JsonOptions);
        document["collisionTimeline"] = "Precomputed for emitted playback joints; changes sampled at <=0.032 degrees summed joint travel and <=0.01 seconds nominal time. Identical contacts compressed. Clear validated paths require no duplicate check.";
        summary = $"{program.ReadyCount} ready / {program.WarningCount} warning / {program.BlockedCount} blocked · {controller.Primitives.Length} commands · {totalSeconds:0.0}s calculation";
        return new { summary, readyCount = program.ReadyCount, warningCount = program.WarningCount, blockedCount = program.BlockedCount,
            durationSeconds = playback.DurationSeconds, program = document, csv = playback.ToCsv(),
            timing = new { planningSeconds, weavingSeconds, postprocessingSeconds = totalSeconds - planningSeconds - weavingSeconds, totalSeconds },
            controller = new { text = controller.Text, fileName = controller.FileName, metadata = controller.Metadata,
                jointMoves = controller.JointMoves, linearMoves = controller.LinearMoves, circularMoves = controller.CircularMoves,
                description = $"CODROID Lua · {controller.JointMoves} joint / {controller.LinearMoves} linear / {controller.CircularMoves} circular moves · torch DO{torchDigitalOutput}. Tool 1 and coordinate system 1 must match the calibrated cell.",
                primitives = controller.Primitives.Select(p => new { command = p.Command, sourceStart = p.SourceStart, sourceEnd = p.SourceEnd, arc = p.ArcOn, seam = p.SeamId, startJoints = p.StartJoints, endJoints = p.EndJoints, viaJoints = p.ViaJoints, speed = p.Speed, duration = p.DurationSeconds }) } };
    }

    internal sealed record CollisionFrame(float T, int[] Links, string[] Reasons);

    /// <summary>Precompute contacts against the exact emitted joint path, removing round-trip latency during playback.</summary>
    internal static CollisionFrame[][] BuildCollisionTimeline(WeldProgram program, IReadOnlyList<WeldMotion> motions,
        WeldPlanRequest request, CancellationToken cancellation = default, Action<float, string>? progress = null)
    {
        var result = new CollisionFrame[motions.Count][];
        var warned = program.Seams.Where(s => s.HasCollision).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        float[] previous = program.StartAngles;
        for (int i = 0; i < motions.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            WeldMotion motion = motions[i];
            progress?.Invoke(i / (float)Math.Max(1, motions.Count), "Preparing collision playback " + motion.SeamId);
            if (!warned.Contains(motion.SeamId))
                result[i] = new[] { new CollisionFrame(0, Array.Empty<int>(), Array.Empty<string>()) };
            else
            {
                float sum = 0;
                for (int axis = 0; axis < 6; axis++) sum += Math.Abs(motion.TargetAngles[axis] - previous[axis]);
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(sum / .032f, motion.DurationSeconds / .01f)));
                var frames = new List<CollisionFrame>();
                var pose = new float[6];
                for (int sample = 0; sample <= steps; sample++)
                {
                    if ((sample & 31) == 0) cancellation.ThrowIfCancellationRequested();
                    float t = sample / (float)steps;
                    for (int axis = 0; axis < 6; axis++) pose[axis] = Mathf.Lerp(previous[axis], motion.TargetAngles[axis], t);
                    request.Collision.CheckDetailed(pose, out _, out CollisionContact[] contacts);
                    int[] links = contacts.SelectMany(c => c.Links).Distinct().OrderBy(link => link).ToArray();
                    string[] reasons = contacts.Select(c => c.Reason).Distinct(StringComparer.Ordinal).OrderBy(reason => reason, StringComparer.Ordinal).ToArray();
                    if (frames.Count == 0 || !frames[^1].Links.SequenceEqual(links) || !frames[^1].Reasons.SequenceEqual(reasons))
                        frames.Add(new CollisionFrame(t, links, reasons));
                }
                result[i] = frames.ToArray();
            }
            previous = motion.TargetAngles;
        }
        progress?.Invoke(1, "Collision playback ready");
        return result;
    }

    private CollisionScene MakeCollision(Prepared input) => new(input.Document.Indices.Select(i => input.PartTransform * input.Document.Vertices[i]).ToArray(), _capsules, staticTriangles: _fixtures, robotTriangles: _robotTriangles);

    // Caller holds _collisionScenesGate. Cached scenes are immutable and checks use per-call scratch memory.
    private void PruneCollisionScenes()
    {
        DateTime cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(15);
        foreach (string id in _collisionScenes.Where(p => p.Value.LastUsedUtc < cutoff).Select(p => p.Key).ToArray()) _collisionScenes.Remove(id);
    }

    private void PruneJobs()
    {
        foreach (Job job in _jobs.Values.OrderBy(j => j.CreatedUtc))
        {
            lock (job.Gate)
            {
                if (job.Status == "running" || (_jobs.Count < 16 && DateTime.UtcNow - job.CreatedUtc < TimeSpan.FromMinutes(30))) continue;
                if (_jobs.TryRemove(job.Id, out _)) job.Cancellation.Dispose();
            }
        }
    }

    private static async Task<T> ReadRequest<T>(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaximumBodyBytes) throw new InvalidDataException("JSON body exceeds the 64 MB bridge limit.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[65536];
        int count;
        while ((count = await request.InputStream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (bytes.Length + count > MaximumBodyBytes) throw new InvalidDataException("JSON body exceeds the 64 MB bridge limit.");
            bytes.Write(buffer, 0, count);
        }
        bytes.Position = 0;
        return await JsonSerializer.DeserializeAsync<T>(bytes, JsonOptions, timeout.Token) ?? throw new InvalidDataException("Empty JSON request.");
    }

    private static async Task Reply(HttpListenerContext context, int status, object body)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed record Prepared(CadDocument Document, CadSeam[] Seams, Transform3D PartTransform, float[] StartAngles, string Name, WeavingOptions Weaving);

    private static Prepared Prepare(BridgeRequest input, bool requireSelectedSeam = false)
    {
        WorkerDocument raw = input.Document ?? throw new InvalidDataException("document is required (the original CAD worker JSON).");
        Vector3[] vertices = Vectors(raw.Vertices, "vertices"), normals = Vectors(raw.Normals, "normals");
        if (vertices.Length < 3 || normals.Length != vertices.Length || raw.Indices == null || raw.Indices.Length < 3 || raw.Indices.Length % 3 != 0 || raw.Indices.Any(i => i < 0 || i >= vertices.Length))
            throw new InvalidDataException("Invalid CAD triangle mesh.");
        if (vertices.Any(v => v.LengthSquared() > 1_000_000)) throw new InvalidDataException("CAD coordinates exceed the supported 1000 metre workspace.");
        Aabb bounds = new(vertices[0], Vector3.Zero);
        foreach (Vector3 vertex in vertices) bounds = bounds.Expand(vertex);
        string name = string.IsNullOrWhiteSpace(input.PartName) ? "CAD workpiece" : input.PartName[..Math.Min(input.PartName.Length, 256)];
        var document = new CadDocument { Name = name, Vertices = vertices, Normals = normals, Indices = raw.Indices, Bounds = bounds };
        if (raw.Seams == null || raw.Seams.Length > 20000) throw new InvalidDataException("Invalid CAD seam count.");
        var deleted = new HashSet<string>(input.DeletedSeamIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        int seamPointCount = 0;
        var knownIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkerSeam source in raw.Seams)
        {
            if (source == null || string.IsNullOrEmpty(source.Id) || source.Id.Length > 256 || !knownIds.Add(source.Id)) throw new InvalidDataException("Invalid or duplicate seam id.");
            Vector3[] points = Vectors(source.Points, "seam points"), a = Vectors(source.NormalsA, "seam normalsA"), b = Vectors(source.NormalsB, "seam normalsB");
            seamPointCount += points.Length;
            if (seamPointCount > 250000 || points.Any(p => p.LengthSquared() > 1_000_000)) throw new InvalidDataException("CAD seam sampling or coordinate limit exceeded.");
            if (a.Any(n => n.LengthSquared() > 4) || b.Any(n => n.LengthSquared() > 4)) throw new InvalidDataException("Invalid CAD seam normals.");
            if (points.Length < 2 || !float.IsFinite(source.AngleDegrees) || !float.IsFinite(source.MinAngleDegrees) || !float.IsFinite(source.MaxAngleDegrees)) continue;
            var seam = new CadSeam { Id = source.Id, Kind = source.Kind ?? "Topology edge", Points = points, NormalsA = a, NormalsB = b,
                NormalA = a.Length > 0 ? a[a.Length / 2] : Vector3.Up, NormalB = b.Length > 0 ? b[b.Length / 2] : Vector3.Right,
                AngleDegrees = source.AngleDegrees, MinAngleDegrees = source.MinAngleDegrees, MaxAngleDegrees = source.MaxAngleDegrees,
                Concave = source.Concave, Deleted = deleted.Contains(source.Id) };
            if (seam.Length > 100) throw new InvalidDataException("A seam exceeds the 100 metre planning limit.");
            if (seam.Length >= .0005f) document.Seams.Add(seam);
        }
        if (!float.IsFinite(input.MinimumAngle) || !float.IsFinite(input.MaximumAngle) || input.MinimumAngle < 0 || input.MaximumAngle > 180 || input.MinimumAngle > input.MaximumAngle)
            throw new InvalidDataException("Invalid seam angle range.");
        if (!float.IsFinite(input.MinimumLengthMm) || !float.IsFinite(input.MaximumLengthMm) || input.MinimumLengthMm < 0 || input.MaximumLengthMm > 100000 || input.MinimumLengthMm > input.MaximumLengthMm)
            throw new InvalidDataException("Invalid seam length range (millimetres).");
        input.Weaving ??= new WeavingOptions();
        input.Weaving.Validate();
        HashSet<string>? included = input.SeamIds == null ? null : new HashSet<string>(input.SeamIds, StringComparer.Ordinal);
        CadSeam[] seams = document.Seams.Where(s => !s.Deleted && (included == null || included.Contains(s.Id)) &&
            s.MinAngleDegrees >= input.MinimumAngle - .05f && s.MaxAngleDegrees <= input.MaximumAngle + .05f &&
            s.Length * 1000 >= input.MinimumLengthMm - .001f && s.Length * 1000 <= input.MaximumLengthMm + .001f &&
            (!input.ConcaveOnly || s.Concave || s.Kind.Contains("contact", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (seams.Length > 256) throw new InvalidDataException("Select at most 256 seam candidates per plan.");
        if (requireSelectedSeam && (included == null || seams.Length == 0)) throw new InvalidDataException("Select at least one seam inside the angle and length filters.");
        Transform3D placement;
        if (input.PartTransform == null)
            placement = new Transform3D(Basis.Identity, new Vector3(.85f, .15f, 0) - new Vector3(bounds.GetCenter().X, bounds.Position.Y, bounds.GetCenter().Z));
        else
        {
            if (input.PartTransform.Position?.Length != 3) throw new InvalidDataException("partTransform.position requires three coordinates.");
            Vector3 position = Vectors(input.PartTransform.Position, "partTransform.position")[0];
            if (position.LengthSquared() > 10000) throw new InvalidDataException("Invalid part position.");
            float[] q = input.PartTransform.Quaternion;
            if (q == null || q.Length != 4 || q.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Invalid part quaternion [x,y,z,w].");
            var quaternion = new Quaternion(q[0], q[1], q[2], q[3]);
            if (!float.IsFinite(quaternion.LengthSquared()) || quaternion.LengthSquared() < .000001f) throw new InvalidDataException("Part quaternion must have a finite nonzero length.");
            placement = new Transform3D(new Basis(quaternion.Normalized()), position);
        }
        float[] start = input.StartAngles ?? (float[])RobotController.HomeAngles.Clone();
        if (start.Length != 6 || start.Any(a => !float.IsFinite(a))) throw new InvalidDataException("startAngles requires six finite joint angles in degrees.");
        return new Prepared(document, seams, placement, start, name, input.Weaving);
    }

    private static Vector3[] Vectors(float[]? values, string label)
    {
        if (values == null || values.Length % 3 != 0 || values.Any(v => !float.IsFinite(v))) throw new InvalidDataException("Invalid " + label + " flat XYZ array.");
        var result = new Vector3[values.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(values[3 * i], values[3 * i + 1], values[3 * i + 2]);
        return result;
    }

    private static object Pose(Transform3D transform)
    {
        Quaternion q = transform.Basis.GetRotationQuaternion();
        return new { position = new[] { transform.Origin.X, transform.Origin.Y, transform.Origin.Z }, quaternion = new[] { q.X, q.Y, q.Z, q.W } };
    }

    private static Dictionary<int, Vector3[]> ReadSource(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "ESM1") throw new InvalidDataException("Invalid original robot mesh.");
        int groups = reader.ReadInt32();
        var links = new Dictionary<int, List<Vector3>>();
        for (int group = 0; group < groups; group++)
        {
            int link = reader.ReadInt32(); reader.ReadBytes(16); int count = reader.ReadInt32();
            if (link < 0 || link > 6 || count < 0 || count > 10_000_000 || count % 3 != 0) throw new InvalidDataException("Invalid original robot mesh group.");
            if (!links.TryGetValue(link, out var vertices)) links[link] = vertices = new List<Vector3>();
            for (int i = 0; i < count; i++) { vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())); reader.ReadBytes(12); }
        }
        return links.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    private sealed class BridgeRequest
    {
        public string? SceneId { get; set; }
        public WorkerDocument? Document { get; set; }
        public string? PartName { get; set; }
        public BridgePose? PartTransform { get; set; }
        public float[]? StartAngles { get; set; }
        public string[]? SeamIds { get; set; }
        public string[]? DeletedSeamIds { get; set; }
        public float MinimumAngle { get; set; } = 85;
        public float MaximumAngle { get; set; } = 95;
        public float MinimumLengthMm { get; set; } = .5f;
        public float MaximumLengthMm { get; set; } = 100000;
        public WeavingOptions Weaving { get; set; } = new();
        public bool ConcaveOnly { get; set; }
    }
    private sealed class ExportRequest
    {
        public bool LinearizeArcs { get; set; }
        public int TorchDigitalOutput { get; set; } = 1;
    }
    private sealed class BridgePose
    {
        public float[] Position { get; set; } = new float[3];
        public float[] Quaternion { get; set; } = new[] { 0f, 0f, 0f, 1f };
    }
    private sealed class WorkerDocument
    {
        public float[] Vertices { get; set; } = Array.Empty<float>();
        public float[] Normals { get; set; } = Array.Empty<float>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public WorkerSeam[] Seams { get; set; } = Array.Empty<WorkerSeam>();
    }
    private sealed class WorkerSeam
    {
        public string Id { get; set; } = "";
        public string Kind { get; set; } = "Topology edge";
        public float[] Points { get; set; } = Array.Empty<float>();
        public float[] NormalsA { get; set; } = Array.Empty<float>();
        public float[] NormalsB { get; set; } = Array.Empty<float>();
        public float AngleDegrees { get; set; }
        public float MinAngleDegrees { get; set; }
        public float MaxAngleDegrees { get; set; }
        public bool Concave { get; set; }
    }
}
