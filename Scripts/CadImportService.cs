using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>Runs the bundled OpenCascade geometry worker; the application, editing and planning remain C#.</summary>
public sealed class CadImportService
{
    public async Task<CadDocument> ImportAsync(string path, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("The STEP file could not be found.", path);
        if (!new[] { ".step", ".stp" }.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidDataException("Choose a STEP file (.step or .stp).");
        if (new FileInfo(path).Length > 256L * 1024 * 1024)
            throw new InvalidDataException("This STEP file exceeds the 256 MB desktop import limit.");
        var (python, worker) = FindRuntime();
        string output = Path.Combine(Path.GetTempPath(), "estun-cad-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            progress?.Report("Reading STEP geometry with OpenCascade…");
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(worker)!
            };
            start.ArgumentList.Add(worker);
            start.ArgumentList.Add("--input");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(output);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the CAD geometry worker.");
            using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            Task stderr = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("PROGRESS ", StringComparison.Ordinal)) progress?.Report(line[9..]);
                }
            }, ct);
            // OpenCascade may print diagnostics on stdout. Drain it to avoid filling a pipe.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            string diagnostics;
            try
            {
                await process.WaitForExitAsync(ct);
                await stderr;
                diagnostics = await stdout;
            }
            catch (OperationCanceledException)
            {
                // Kill is asynchronous: reap the process before deleting its output.
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
                try { await stderr; } catch (OperationCanceledException) { }
                try { await stdout; } catch (OperationCanceledException) { }
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || !File.Exists(output))
                throw new InvalidDataException("OpenCascade could not import this STEP file. " + (diagnostics.Length > 1600 ? diagnostics[^1600..] : diagnostics));
            if (new FileInfo(output).Length > 320L * 1024 * 1024)
                throw new InvalidDataException("The tessellated CAD model is too large for interactive use. Simplify the source model first.");
            progress?.Report("Preparing CAD mesh and seam candidates…");
            await using var stream = File.OpenRead(output);
            var data = await JsonSerializer.DeserializeAsync<WorkerDocument>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct)
                ?? throw new InvalidDataException("CAD worker returned empty geometry.");
            return await Task.Run(() => Convert(data, path), ct);
        }
        finally { try { File.Delete(output); } catch (IOException) { } }
    }

    public static (string Python, string Worker) FindRuntime()
    {
        string[] roots = { AppContext.BaseDirectory, Path.GetDirectoryName(OS.GetExecutablePath()) ?? "", ProjectSettings.GlobalizePath("res://"), Directory.GetCurrentDirectory() };
        foreach (string root in roots.Distinct())
        {
            foreach (string folder in new[] { "CadRuntime", ".tools/cad-python" })
            {
                string python = Path.Combine(root, folder, "python.exe");
                string worker = Path.Combine(root, "tools", "cad_import.py");
                if (!File.Exists(worker)) worker = Path.Combine(root, folder, "cad_import.py");
                if (File.Exists(python) && File.Exists(worker)) return (python, worker);
            }
        }
        throw new FileNotFoundException("CAD runtime is missing. Run tools/SetupCad.ps1 once, or place the supplied CadRuntime folder beside ESTUN Studio.exe.");
    }

    private static CadDocument Convert(WorkerDocument raw, string path)
    {
        Vector3[] vertices = Vectors(raw.Vertices), normals = Vectors(raw.Normals);
        if (vertices.Length < 3 || normals.Length != vertices.Length || raw.Indices.Length < 3 || raw.Indices.Length % 3 != 0 || raw.Indices.Any(i => i < 0 || i >= vertices.Length))
            throw new InvalidDataException("CAD worker returned an invalid triangle mesh.");
        Aabb bounds = new(vertices[0], Vector3.Zero);
        foreach (Vector3 v in vertices) bounds = bounds.Expand(v);
        var document = new CadDocument { Name = Path.GetFileNameWithoutExtension(path), SourcePath = path, SourceUnit = raw.SourceUnit, Vertices = vertices, Normals = normals, Indices = raw.Indices, Bounds = bounds, SolidCount = raw.SolidCount, FaceCount = raw.FaceCount, Warnings = raw.Warnings };
        foreach (WorkerSeam edge in raw.Seams)
        {
            Vector3[] points = Vectors(edge.Points), na = Vectors(edge.NormalsA), nb = Vectors(edge.NormalsB);
            if (points.Length < 2 || !float.IsFinite(edge.AngleDegrees) || !float.IsFinite(edge.MinAngleDegrees) || !float.IsFinite(edge.MaxAngleDegrees)) continue;
            var seam = new CadSeam { Id = edge.Id, Kind = edge.Kind, Points = points, NormalA = na.Length > 0 ? na[na.Length / 2] : Vector3.Up, NormalB = nb.Length > 0 ? nb[nb.Length / 2] : Vector3.Right, NormalsA = na, NormalsB = nb, AngleDegrees = edge.AngleDegrees, MinAngleDegrees = edge.MinAngleDegrees, MaxAngleDegrees = edge.MaxAngleDegrees, Concave = edge.Concave };
            if (seam.Length >= .0005f) document.Seams.Add(seam);
        }
        return document;
    }

    private static Vector3[] Vectors(float[] data)
    {
        if (data.Length % 3 != 0 || data.Any(x => !float.IsFinite(x))) throw new InvalidDataException("CAD contains invalid coordinates.");
        var result = new Vector3[data.Length / 3];
        for (int i = 0; i < result.Length; i++) result[i] = new Vector3(data[i * 3], data[i * 3 + 1], data[i * 3 + 2]);
        return result;
    }

    private sealed class WorkerDocument
    {
        public float[] Vertices { get; set; } = Array.Empty<float>();
        public float[] Normals { get; set; } = Array.Empty<float>();
        public int[] Indices { get; set; } = Array.Empty<int>();
        public string SourceUnit { get; set; } = "mm";
        public int SolidCount { get; set; }
        public int FaceCount { get; set; }
        public WorkerSeam[] Seams { get; set; } = Array.Empty<WorkerSeam>();
        public string[] Warnings { get; set; } = Array.Empty<string>();
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
