using Godot;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EstunStudio;

/// <summary>Exercises the shipping C# service, isolated native runtime, STEP parsing and cancellation.</summary>
public partial class CadImportChecks : Node
{
    private int _checks;
    public override async void _Ready()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ESTUN CAD checks " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var service = new CadImportService();
            var runtime = CadImportService.FindRuntime();
            Require(File.Exists(runtime.Python) && File.Exists(runtime.Worker), "Bundled CAD runtime and worker discovered");
            string path = Path.Combine(directory, "bracket with spaces (test).STEP");
            File.Copy(ProjectSettings.GlobalizePath("res://Assets/Samples/WeldingBracket.step"), path);
            CadDocument document = await service.ImportAsync(path, null, timeout.Token);
            Require(document.Name == "bracket with spaces (test)", "Process arguments preserve paths containing spaces and parentheses");
            Require(document.SolidCount == 3 && document.FaceCount == 18, "Real STEP assembly has three closed solids and eighteen BRep faces");
            Require(document.TriangleCount == 36 && document.Vertices.Length == document.Normals.Length, "C# receives valid triangle mesh and normals");
            Require(document.Bounds.Size.IsEqualApprox(new Vector3(.25f, .125f, .19f)), "STEP dimensions normalize to metres and Y-up");
            Require(document.Seams.Count(s => s.Kind == "Part contact") >= 8, "Contact seams are recognized across separate solids");
            Require(document.Seams.All(s => s.MatchesAngleRange(85, 95)) && !document.Seams.Any(s => s.MatchesAngleRange(40, 50)), "User angle window filters exact BRep face angles");
            var seam = document.Seams.First();
            Require(seam.Points.Length > 2 && seam.NormalsA.Length == seam.Points.Length && seam.NormalsB.Length == seam.Points.Length, "Seams carry tracking polylines and sampled adjacent normals");
            seam.Deleted = true;
            Require(!seam.MatchesAngleRange(85, 95) && !seam.Enabled, "Deleted seam stays excluded from planning");
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            bool cancellationObserved = false;
            try { await service.ImportAsync(path, null, cancelled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Require(cancellationObserved, "Cancelled import terminates the worker and does not replace CAD");
            string bad = Path.Combine(directory, "malformed.step");
            await File.WriteAllTextAsync(bad, "This is not STEP data");
            bool invalidRejected = false;
            try { await service.ImportAsync(bad, null, timeout.Token); }
            catch (InvalidDataException) { invalidRejected = true; }
            Require(invalidRejected, "Malformed STEP is reported as an import error");
            GD.Print($"PASS: {_checks} C# CAD service integration checks");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PrintErr("FAIL: " + exception);
            GetTree().Quit(1);
        }
        finally
        {
            // The directory is generated above, never supplied by the user.
            foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _checks++;
        GD.Print("  OK  " + description);
    }
}
