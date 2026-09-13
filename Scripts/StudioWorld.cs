using Godot;
using System;

namespace EstunStudio;

/// <summary>Photographic lighting stage with real HDR reflections and material detail.</summary>
public partial class StudioWorld : Node3D
{
    public Godot.Environment Environment { get; private set; } = null!;
    public Node3D RobotMount { get; private set; } = null!;
    public Node3D TcpAxes { get; private set; } = null!;
    private MeshInstance3D _trail = null!;
    private readonly System.Collections.Generic.List<Vector3> _points = new();
    private readonly StandardMaterial3D _trailMaterial = Emissive(new Color("58c8be"), 1.5f);
    private bool _tracing;
    public bool ShowCoordinates { get => TcpAxes.Visible; set => TcpAxes.Visible = value; }
    public bool ShowTrail { get => _tracing; set { _tracing = value; _trail.Visible = value; if(!value) _points.Clear(); } }

    public void Build()
    {
        BuildLighting();
        var concrete = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Concrete.gdshader") };
        Box(this, new Vector3(50, .15f, 50), new Vector3(0,-.18f,0), concrete, "Polished concrete");
        var dark = Metal("252a30", .55f, .30f);
        var steel = Metal("6d7780", .84f, .23f);
        var black = Metal("10151a", .3f, .36f);
        var wall = Metal("30353c", .28f, .58f);
        // Architectural panels, recessed joints and warm service lights.
        for(int i = -18; i <= 18; i++)
        {
            Box(this,new Vector3(1.38f,5,.13f),new Vector3(i*1.42f,2.38f,-5.6f),wall);
            Box(this,new Vector3(.018f,4.8f,.02f),new Vector3(i*1.42f+.70f,2.4f,-5.50f),black);
            Box(this,new Vector3(1.38f,5,.13f),new Vector3(i*1.42f,2.38f,5.6f),wall);
            Box(this,new Vector3(.018f,4.8f,.02f),new Vector3(i*1.42f+.70f,2.4f,5.50f),black);
        }
        Box(this,new Vector3(30,.11f,.22f),new Vector3(0,.12f,5.38f),black);
        Box(this,new Vector3(28,.022f,.025f),new Vector3(0,.20f,5.24f),Emissive(new Color("bacbd7"),1.2f));
        Box(this,new Vector3(20,.11f,.22f),new Vector3(0,.12f,-5.38f),black);
        Box(this,new Vector3(18,.022f,.025f),new Vector3(0,.20f,-5.24f),Emissive(new Color("bacbd7"),1.6f));
        for(int i=0;i<3;i++)
        {
            Box(this,new Vector3(2.6f,.06f,.06f),new Vector3(-3+i*3.4f,3.9f,-5.43f),Emissive(new Color("d4e3eb"),2.3f));
        }
        // Machined instrument plinth, elastomer gasket, and aluminum top plate.
        var plinth=new Node3D {Name="Instrument plinth",Scale=new Vector3(.70f,1,.70f)};AddChild(plinth);
        Cylinder(plinth,1.00f,.13f,new Vector3(0,-.025f,0),black);
        Cylinder(plinth,.91f,.055f,new Vector3(0,.067f,0),steel);
        Cylinder(plinth,.83f,.016f,new Vector3(0,.103f,0),dark);
        Ring(plinth,.915f,.011f,.007f,new Vector3(0,.071f,0),Emissive(new Color("f4b64d"),2.0f));
        for(int i=0;i<24;i++)
        {
            float a=i*Mathf.Tau/24;
            var p=new Vector3(Mathf.Cos(a)*.865f,.102f,Mathf.Sin(a)*.865f);
            Cylinder(plinth,.012f,.010f,p,black,6);
            if(i%2==0)
            {
                var marker=Box(plinth,new Vector3(.002f,.001f,.046f),p+new Vector3(0,.007f,0),steel);
                marker.Rotation=new Vector3(0,-a+Mathf.Pi/2,0);
            }
        }
        RobotMount=new Node3D {Name="Robot mounting plane",Position=new Vector3(0,.12f,0)};
        AddChild(RobotMount);
        // Floor outlines establish scale without a distracting wireframe grid.
        var paint=Metal("747f87",.2f,.54f);
        for(int i=-4;i<=4;i++)
        {
            Box(this,new Vector3(.008f,.001f,9),new Vector3(i,-.101f,0),paint);
            Box(this,new Vector3(9,.001f,.008f),new Vector3(0,-.101f,i),paint);
        }
        var amber=Metal("be914b",.25f,.44f);
        foreach(var corner in new[]{new Vector3(-1.4f,-.099f,-1.4f),new Vector3(1.4f,-.099f,-1.4f),new Vector3(-1.4f,-.099f,1.4f),new Vector3(1.4f,-.099f,1.4f)})
        {
            Box(this,new Vector3(.42f,.002f,.022f),corner,amber);
            Box(this,new Vector3(.022f,.002f,.42f),corner,amber);
        }
        // Instrument console in the background, deliberately understated.
        Box(this,new Vector3(.9f,1.65f,.64f),new Vector3(-2.6f,.725f,-2.9f),dark);
        Box(this,new Vector3(.78f,1.5f,.018f),new Vector3(-2.6f,.75f,-2.566f),black);
        for(int i=0;i<12;i++) Box(this,new Vector3(.58f,.012f,.024f),new Vector3(-2.6f,.24f+i*.033f,-2.55f),steel);
        Box(this,new Vector3(.54f,.15f,.024f),new Vector3(-2.6f,1.24f,-2.55f),Metal("293e40",.25f,.20f));
        Box(this,new Vector3(.045f,.016f,.028f),new Vector3(-2.81f,1.42f,-2.54f),Emissive(new Color("73d4b1"),2));
        var logo = new Label3D {Text="ESTUN",FontSize=100,PixelSize=.0075f,Modulate=new Color("75818a"),OutlineSize=0,Position=new Vector3(1.1f,2.6f,-5.46f)};
        AddChild(logo);
        var caption = new Label3D {Text="PRECISION IN MOTION",FontSize=36,PixelSize=.006f,Modulate=new Color("616d77"),OutlineSize=0,Position=new Vector3(1.1f,2.1f,-5.45f)};
        AddChild(caption);
        TcpAxes = new Node3D {Name="TCP coordinate frame"};
        AddChild(TcpAxes);
        Axis(TcpAxes,Vector3.Right,new Color("f57565"));
        Axis(TcpAxes,Vector3.Up,new Color("8acd9d"));
        Axis(TcpAxes,Vector3.Back,new Color("78adf4"));
        _trail=new MeshInstance3D {Name="TCP path",CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};
        AddChild(_trail);
    }

    public void UpdateTcp(Transform3D transform, bool moving)
    {
        TcpAxes.GlobalTransform=transform;
        if(!_tracing || !moving) return;
        Vector3 p=transform.Origin;
        if(_points.Count>0 && _points[^1].DistanceTo(p)<.006f) return;
        _points.Add(p);
        if(_points.Count>1200) _points.RemoveAt(0);
        if(_points.Count<2) return;
        var mesh=new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip,_trailMaterial);
        foreach(var v in _points) mesh.SurfaceAddVertex(v);
        mesh.SurfaceEnd();
        _trail.Mesh=mesh;
    }

    private void BuildLighting()
    {
        var skyMat=new PanoramaSkyMaterial {Panorama=GD.Load<Texture2D>("res://Assets/Textures/studio_small_09_2k.hdr"),EnergyMultiplier=.55f};
        Environment=new Godot.Environment
        {
            BackgroundMode=Godot.Environment.BGMode.Color,BackgroundColor=new Color("242b33"),
            Sky=new Sky {SkyMaterial=skyMat,RadianceSize=Sky.RadianceSizeEnum.Size1024},
            AmbientLightSource=Godot.Environment.AmbientSource.Sky,AmbientLightEnergy=.50f,
            ReflectedLightSource=Godot.Environment.ReflectionSource.Sky,
            TonemapMode=Godot.Environment.ToneMapper.Aces,TonemapExposure=.92f,
            SsaoEnabled=true,SsaoRadius=.25f,SsaoIntensity=1.0f,SsaoPower=1.2f,
            SsilEnabled=true,SsilIntensity=.30f,SsilRadius=2,
            SsrEnabled=true,SsrMaxSteps=96,SsrFadeIn=.1f,SsrFadeOut=1.4f,SsrDepthTolerance=.3f,
            GlowEnabled=true,GlowIntensity=.30f,GlowBloom=.035f,GlowHdrThreshold=1.5f,
            FogEnabled=true,FogLightColor=new Color("7d8c9b"),FogDensity=.003f,FogSkyAffect=0
        };
        AddChild(new WorldEnvironment {Environment=Environment});
        Spot("Key softbox",new Vector3(1.6f,5,3.2f),new Vector3(.6f,1.0f,0),new Color("f4e7d2"),1.4f,58,12,1.1f);
        Spot("Cold rim",new Vector3(-1.0f,3.8f,-3.2f),new Vector3(.7f,1.4f,0),new Color("a0c7fa"),2.2f,60,10,.7f);
        Spot("Front fill",new Vector3(4.0f,2.8f,1.5f),new Vector3(.8f,1,0),new Color("d8e8f3"),.65f,65,9,1.5f);
        Spot("Left bounce",new Vector3(-3.2f,2.5f,1.2f),new Vector3(0,1.0f,0),new Color("fff0d3"),.8f,64,10,.8f);
        var sun=new DirectionalLight3D {Name="Ceiling bounce",RotationDegrees=new Vector3(-68,-25,0),LightColor=new Color("b9c6d0"),LightEnergy=.20f,ShadowEnabled=true,DirectionalShadowMaxDistance=18,LightAngularDistance=.5f};
        AddChild(sun);
        var probe=new ReflectionProbe {Name="Stage reflections",Position=new Vector3(.5f,1.5f,0),Size=new Vector3(14,8,14),Intensity=.8f,BoxProjection=true,Interior=false,UpdateMode=ReflectionProbe.UpdateModeEnum.Once};
        AddChild(probe);
    }
    public void SetUltra(bool ultra)
    {
        Environment.SsaoEnabled=ultra; Environment.SsilEnabled=ultra; Environment.SsrEnabled=ultra;
        GetViewport().Msaa3D=ultra?Viewport.Msaa.Msaa4X:Viewport.Msaa.Disabled;
        GetViewport().UseTaa=false;
    }
    private void Spot(string name,Vector3 pos,Vector3 target,Color color,float energy,float angle,float range,float size)
    {
        var light=new SpotLight3D {Name=name,Position=pos,LightColor=color,LightEnergy=energy,SpotAngle=angle,SpotRange=range,SpotAttenuation=.65f,ShadowEnabled=true,LightSize=size*.09f,ShadowBias=.025f,ShadowNormalBias=.5f};
        AddChild(light); light.LookAt(target,Vector3.Up);
    }
    private static void Axis(Node3D parent,Vector3 direction,Color color)
    {
        var mat=Emissive(color,.6f);
        var rod=Cylinder(parent,.0024f,.18f,direction*.09f,mat,12);
        rod.Quaternion=new Quaternion(Vector3.Up,direction);
        var cone=new MeshInstance3D {Mesh=new CylinderMesh {TopRadius=0,BottomRadius=.011f,Height=.035f,RadialSegments=20},MaterialOverride=mat,Position=direction*.195f,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};
        parent.AddChild(cone);cone.Quaternion=new Quaternion(Vector3.Up,direction);
    }
    public static StandardMaterial3D Metal(string hex,float metallic,float roughness) => new() {AlbedoColor=new Color(hex),Metallic=metallic,Roughness=roughness};
    public static StandardMaterial3D Emissive(Color color,float power) => new() {AlbedoColor=color,EmissionEnabled=true,Emission=color,EmissionEnergyMultiplier=power,Roughness=.3f};
    public static MeshInstance3D Box(Node3D parent,Vector3 size,Vector3 pos,Material mat,string name="Panel")
    {
        var mesh=new MeshInstance3D {Name=name,Mesh=new BoxMesh {Size=size},MaterialOverride=mat,Position=pos};parent.AddChild(mesh);return mesh;
    }
    public static MeshInstance3D Cylinder(Node3D parent,float radius,float height,Vector3 pos,Material mat,int segments=96)
    {
        var mesh=new MeshInstance3D {Mesh=new CylinderMesh {TopRadius=radius,BottomRadius=radius,Height=height,RadialSegments=segments},MaterialOverride=mat,Position=pos};parent.AddChild(mesh);return mesh;
    }
    public static void Ring(Node3D parent,float radius,float tube,float height,Vector3 pos,Material mat)
    {
        var mesh=new MeshInstance3D {Mesh=new TorusMesh {InnerRadius=radius-tube,OuterRadius=radius+tube,Rings=128,RingSegments=12},MaterialOverride=mat,Position=pos};parent.AddChild(mesh);
    }
}
