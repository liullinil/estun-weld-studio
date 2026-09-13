using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
namespace EstunStudio;

public partial class CadPart : Node3D
{
    public CadDocument Document {get;private set;}=null!;
    public event Action? SeamSelectionChanged;
    public string? SelectedSeam {get;private set;}
    public float MinimumAngle {get;set;}=85;
    public float MaximumAngle {get;set;}=95;
    public bool ConcaveOnly {get;set;}=false;
    private readonly Dictionary<string,MeshInstance3D> _seamMeshes=new();
    private readonly Dictionary<string,int> _states=new();
    private MeshInstance3D _mesh=null!;
    private Camera3D _camera=null!;
    public void Build(CadDocument document,Camera3D camera)
    {
        Document=document;_camera=camera;
        var arrays=new Godot.Collections.Array();arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex]=document.Vertices;arrays[(int)Mesh.ArrayType.Normal]=document.Normals;
        arrays[(int)Mesh.ArrayType.Index]=document.Indices;
        var mesh=new ArrayMesh();mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        _mesh=new MeshInstance3D {Name="STEP workpiece",Mesh=mesh,MaterialOverride=new StandardMaterial3D {AlbedoColor=new Color("778997"),Metallic=.68f,Roughness=.36f,CullMode=BaseMaterial3D.CullModeEnum.Disabled}};AddChild(_mesh);
        RefreshSeams();
    }
    public List<CadSeam> Candidates()=>Document.Seams.Where(s=>s.MatchesAngleRange(MinimumAngle,MaximumAngle)&&(!ConcaveOnly||s.Concave||s.Kind.Contains("contact",StringComparison.OrdinalIgnoreCase))).ToList();
    public void ResetResults(){_states.Clear();RefreshSeams();}
    public void SetResult(string id,bool reachable){_states[id]=reachable?1:-1;}
    public void RefreshSeams()
    {
        foreach(var mesh in _seamMeshes.Values)mesh.QueueFree();_seamMeshes.Clear();
        foreach(var seam in Candidates())
        {
            Color color=seam.Id==SelectedSeam?new Color("fff1b2"):_states.TryGetValue(seam.Id,out var state)?state>0?new Color("75deb6"):new Color("f16b72"):new Color("f6b94b");
            var mat=new StandardMaterial3D {AlbedoColor=color,EmissionEnabled=true,Emission=color,EmissionEnergyMultiplier=.7f,ShadingMode=BaseMaterial3D.ShadingModeEnum.Unshaded,NoDepthTest=false};
            var mesh=Tube(seam.Points,.0015f,mat);AddChild(mesh);_seamMeshes.Add(seam.Id,mesh);
        }
    }
    public void Select(string? id){SelectedSeam=id;RefreshSeams();SeamSelectionChanged?.Invoke();}
    public bool DeleteSelected()
    {
        var seam=Document.Seams.FirstOrDefault(s=>s.Id==SelectedSeam);if(seam==null)return false;
        seam.Deleted=true;SelectedSeam=null;RefreshSeams();SeamSelectionChanged?.Invoke();return true;
    }
    public void Restore(){foreach(var seam in Document.Seams)seam.Deleted=false;_states.Clear();RefreshSeams();SeamSelectionChanged?.Invoke();}
    public bool PickSeam(Vector2 mouse)
    {
        float best=11;string? id=null;
        foreach(var seam in Candidates())for(int i=1;i<seam.Points.Length;i++)
        {
            Vector3 a=GlobalTransform*seam.Points[i-1],b=GlobalTransform*seam.Points[i];
            if(_camera.IsPositionBehind(a)||_camera.IsPositionBehind(b))continue;
            float distance=TransformGizmo.SegmentDistance(mouse,_camera.UnprojectPosition(a),_camera.UnprojectPosition(b));
            if(distance<best){best=distance;id=seam.Id;}
        }
        if(id==null)return false;Select(id);return true;
    }
    public static MeshInstance3D Tube(Vector3[] points,float radius,Material material)
    {
        const int sides=6;var vertices=new List<Vector3>();var normals=new List<Vector3>();
        for(int i=1;i<points.Length;i++)
        {
            Vector3 tangent=(points[i]-points[i-1]).Normalized();if(tangent.LengthSquared()<.1f)continue;
            Vector3 u=tangent.Cross(Mathf.Abs(tangent.Dot(Vector3.Up))<.95f?Vector3.Up:Vector3.Right).Normalized(),v=tangent.Cross(u);
            for(int j=0;j<sides;j++)
            {
                Vector3 n0=u*Mathf.Cos(j*Mathf.Tau/sides)+v*Mathf.Sin(j*Mathf.Tau/sides),n1=u*Mathf.Cos((j+1)*Mathf.Tau/sides)+v*Mathf.Sin((j+1)*Mathf.Tau/sides);
                Vector3 a=points[i-1]+n0*radius,b=points[i]+n0*radius,c=points[i]+n1*radius,d=points[i-1]+n1*radius;
                vertices.AddRange(new[]{a,c,b,a,d,c});normals.AddRange(new[]{n0,n1,n0,n0,n1,n1});
            }
        }
        var arrays=new Godot.Collections.Array();arrays.Resize((int)Mesh.ArrayType.Max);arrays[(int)Mesh.ArrayType.Vertex]=vertices.ToArray();arrays[(int)Mesh.ArrayType.Normal]=normals.ToArray();
        var mesh=new ArrayMesh();if(vertices.Count>0)mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
        return new MeshInstance3D {Mesh=mesh,MaterialOverride=material,CastShadow=GeometryInstance3D.ShadowCastingSetting.Off};
    }
}
