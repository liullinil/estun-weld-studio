using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace EstunStudio;

/// <summary>Optional GPU measurement: same scene and display, only window state and 3D resolution policy change.</summary>
public partial class RenderBenchmark : Node
{
    public override async void _Ready()
    {
        try
        {
            var main=GD.Load<PackedScene>("res://Scenes/Main.tscn").Instantiate<Main>();AddChild(main);
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            var effects=Descendants(main).OfType<EffectsPanel>().Single();
            RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(),true);
            var results=new List<object>();
            foreach(var phase in new[]{("Window native",Window.ModeEnum.Windowed,RenderResolution.Native),("Maximized native",Window.ModeEnum.Maximized,RenderResolution.Native),("Maximized auto",Window.ModeEnum.Maximized,RenderResolution.Auto),("Fullscreen native",Window.ModeEnum.Fullscreen,RenderResolution.Native),("Fullscreen auto",Window.ModeEnum.Fullscreen,RenderResolution.Auto),("Window auto",Window.ModeEnum.Windowed,RenderResolution.Auto)})
            {
                GetWindow().Mode=phase.Item2;
                if(phase.Item2==Window.ModeEnum.Windowed)GetWindow().Size=new Vector2I(1440,900);
                effects.Settings.ResolutionMode=phase.Item3;effects.ApplySettings(false);
                ulong start=Time.GetTicksUsec();
                while(Time.GetTicksUsec()-start<3_000_000)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                var times=new List<double>();var gpuTimes=new List<double>();var cpuTimes=new List<double>();start=Time.GetTicksUsec();ulong previous=start;
                while(Time.GetTicksUsec()-start<4_000_000)
                {
                    await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);ulong now=Time.GetTicksUsec();times.Add((now-previous)/1000.0);previous=now;
                    gpuTimes.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()));
                    cpuTimes.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(GetViewport().GetViewportRid()));
                }
                times.Sort();Vector2I size=RenderResolution.GetRenderSize(GetViewport());float scale=GetViewport().Scaling3DScale;
                var record=new {phase=phase.Item1,width=size.X,height=size.Y,scale,fps=1000/times.Average(),frameP95ms=times[(int)(times.Count*.95)],gpuMs=gpuTimes.Average(),renderCpuMs=cpuTimes.Average(),frames=times.Count,focused=GetWindow().HasFocus()};
                results.Add(record);GD.Print(JsonSerializer.Serialize(record));
                if(phase.Item1=="Fullscreen auto")
                {
                    await ToSignal(RenderingServer.Singleton,RenderingServer.SignalName.FramePostDraw);
                    GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath("res://artifacts/fullscreen-auto.png"));
                }
            }
            System.IO.File.WriteAllText(ProjectSettings.GlobalizePath("res://artifacts/fullscreen-performance.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
            GetTree().Quit();
        }
        catch(Exception ex){GD.PrintErr(ex);GetTree().Quit(1);}
    }
    private static IEnumerable<Node> Descendants(Node node){foreach(Node child in node.GetChildren()){yield return child;foreach(Node nested in Descendants(child))yield return nested;}}
}
