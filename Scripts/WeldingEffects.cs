using Godot;
using System;
using System.Collections.Generic;
namespace EstunStudio;

/// <summary>Arc, incandescent spatter, cooling weld pool and visible deposited weld beads.</summary>
public partial class WeldingEffects : Node3D
{
    public bool SparksEnabled {get;set;}=true;
    public bool SmokeEnabled {get;set;}=true;
    public int BeadDetail {get;set;}=2;
    public bool IsArcActive=>_active;
    public int BeadCount=>_beads.Count;
    private OmniLight3D _arcLight=null!;
    private MeshInstance3D _arc=null!;
    private GpuParticles3D _sparks=null!,_smoke=null!;
    private readonly List<MeshInstance3D> _beads=new();
    private readonly List<(StandardMaterial3D Material,float Time)> _hot=new();
    private Vector3? _last;
    private float _time;
    private bool _active;
    public void Build()
    {
        _arcLight=new OmniLight3D {LightColor=new Color("b5d6ff"),LightEnergy=2,OmniRange=1.2f,ShadowEnabled=false};AddChild(_arcLight);
        _arc=new MeshInstance3D {Mesh=new SphereMesh {Radius=.004f,Height=.007f,RadialSegments=24,Rings=12},MaterialOverride=StudioWorld.Emissive(new Color("c5e4ff"),12),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};AddChild(_arc);
        var sparkMaterial=StudioWorld.Emissive(new Color("ffc468"),7);
        _sparks=new GpuParticles3D
        {
            Amount=120,Lifetime=.55,Explosiveness=0,Randomness=.8f,Emitting=false,
            DrawPass1=new SphereMesh {Radius=.0007f,Height=.006f,RadialSegments=6,Rings=3,Material=sparkMaterial},
            ProcessMaterial=new ParticleProcessMaterial {Direction=Vector3.Up,Spread=72,InitialVelocityMin=.25f,InitialVelocityMax=1.4f,Gravity=new Vector3(0,-3,0),ScaleMin=.4f,ScaleMax=1.3f},
            VisibilityAabb=new Aabb(new Vector3(-2,-2,-2),new Vector3(4,4,4)),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off
        };AddChild(_sparks);
        var smokeMaterial=new StandardMaterial3D {AlbedoColor=new Color(.38f,.43f,.49f,.035f),Transparency=BaseMaterial3D.TransparencyEnum.Alpha,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,CullMode=BaseMaterial3D.CullModeEnum.Disabled};
        _smoke=new GpuParticles3D {Amount=32,Lifetime=1.5,Emitting=false,Randomness=.65f,
            DrawPass1=new SphereMesh {Radius=.016f,Height=.028f,RadialSegments=8,Rings=4,Material=smokeMaterial},
            ProcessMaterial=new ParticleProcessMaterial {Direction=Vector3.Up,Spread=16,InitialVelocityMin=.04f,InitialVelocityMax=.10f,Gravity=new Vector3(.015f,.055f,0),ScaleMin=.6f,ScaleMax=1.6f},
            VisibilityAabb=new Aabb(new Vector3(-1,-1,-1),new Vector3(2,2,2)),CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};AddChild(_smoke);
        SetArc(false,Vector3.Zero,Vector3.Up);
    }
    public void SetArc(bool active,Vector3 point,Vector3 normal)
    {
        if(_arc==null)return;
        _active=active;_arc.Visible=active;_arcLight.Visible=active;
        _sparks.Emitting=active&&SparksEnabled;_smoke.Emitting=active&&SmokeEnabled;
        _arc.GlobalPosition=point;_arcLight.GlobalPosition=point+normal*.025f;_sparks.GlobalPosition=point;_smoke.GlobalPosition=point;
        if(!active){_last=null;return;}
        if(_last.HasValue && _last.Value.DistanceTo(point)>=(BeadDetail>=2?.0018f:.003f) && _last.Value.DistanceTo(point)<.025f)
        {
            Deposit(_last.Value,point,normal);_last=point;
        }
        else if(!_last.HasValue || _last.Value.DistanceTo(point)>=.025f)_last=point;
    }
    private void Deposit(Vector3 a,Vector3 b,Vector3 normal)
    {
        if(_beads.Count>=6000)return;
        var mat=new StandardMaterial3D {AlbedoColor=new Color("9ba4ac"),Metallic=.80f,Roughness=.28f,EmissionEnabled=true,Emission=new Color("ff742c"),EmissionEnergyMultiplier=3};
        var bead=new MeshInstance3D {Mesh=new CapsuleMesh {Radius=.0026f,Height=Mathf.Max(.0053f,a.DistanceTo(b)+.0026f),RadialSegments=BeadDetail>=2?16:8,Rings=3},MaterialOverride=mat};
        AddChild(bead);bead.GlobalPosition=(a+b)*.5f+normal*.0008f;bead.Quaternion=new Quaternion(Vector3.Up,(b-a).Normalized());
        _beads.Add(bead);_hot.Add((mat,_time));
    }
    public void Clear(){foreach(var bead in _beads)bead.QueueFree();_beads.Clear();_hot.Clear();_last=null;}
    public override void _Process(double delta)
    {
        _time+=(float)delta;
        if(_active)_arcLight.LightEnergy=1.3f+(Mathf.Sin(_time*137)+1)*.8f;
        for(int i=_hot.Count-1;i>=0;i--)
        {
            var (mat,born)=_hot[i];float heat=Mathf.Clamp(1-(_time-born)/3.5f,0,1);mat.EmissionEnergyMultiplier=heat*heat*3;
            mat.AlbedoColor=new Color("7c8997").Lerp(new Color("e7a15d"),heat*.4f);
            if(heat<=0){mat.EmissionEnabled=false;_hot.RemoveAt(i);}
        }
    }
}
