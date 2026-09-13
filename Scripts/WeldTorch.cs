using Godot;
namespace EstunStudio;

/// <summary>Illustrative water-cooled MIG/MAG torch. All dimensions in metres.</summary>
public partial class WeldTorch : Node3D
{
    public static Transform3D ToolTransform => new(new Basis(Vector3.Back, Mathf.DegToRad(30)), new Vector3(.100f,-.2532f,0));
    public static RobotCapsule[] CollisionVolumes()=>new[]
    {
        new RobotCapsule(7,new(0,-.006f,0),new(0,-.006f,0),.069f,new Aabb(new(-.048f,-.012f,-.048f),new(.096f,.012f,.096f))),
        new RobotCapsule(7,new(0,-.006f,0),new(0,-.070f,0),.040f),
        new RobotCapsule(7,new(0,-.070f,0),new(.050f,-.1666f,0),.020f),
        new RobotCapsule(7,new(.050f,-.1666f,0),new(.086f,-.229f,0),.014f),
        new RobotCapsule(7,new(-.024f,-.023f,.019f),new(-.024f,.016f,.026f),.006f),
        new RobotCapsule(7,new(.024f,-.023f,.019f),new(.024f,.016f,.026f),.006f)
    };
    public void Build()
    {
        var steel=StudioWorld.Metal("8295a3",.85f,.23f);
        var graphite=StudioWorld.Metal("17242d",.3f,.36f);
        var copper=StudioWorld.Metal("bc754a",.78f,.29f);
        var ceramic=StudioWorld.Metal("d3c6a8",.06f,.48f);
        var blue=StudioWorld.Metal("237c92",.4f,.32f);
        Segment(Vector3.Zero,new Vector3(0,-.012f,0),.048f,steel);
        Segment(new(0,-.012f,0),new(0,-.064f,0),.032f,graphite);
        Segment(new(0,-.020f,0),new(0,-.034f,0),.034f,blue);
        for(int i=0;i<6;i++)
        {
            float a=i*Mathf.Tau/6;
            Segment(new(Mathf.Cos(a)*.040f,0,Mathf.Sin(a)*.040f),new(Mathf.Cos(a)*.040f,-.015f,Mathf.Sin(a)*.040f),.004f,graphite);
        }
        Vector3[] neck={new(0,-.056f,0),new(0,-.090f,0),new(.010f,-.114f,0),new(.035f,-.144f,0),new(.054f,-.174f,0)};
        for(int i=1;i<neck.Length;i++){Segment(neck[i-1],neck[i],.013f,steel); Ball(neck[i],.013f,steel);}
        Segment(new(.046f,-.160f,0),new(.063f,-.189f,0),.020f,graphite);
        Segment(new(.060f,-.184f,0),new(.070f,-.201f,0),.017f,ceramic);
        Segment(new(.066f,-.194f,0),new(.087f,-.231f,0),.014f,copper);
        Segment(new(.087f,-.231f,0),new(.091f,-.238f,0),.006f,graphite);
        Segment(new(.089f,-.234f,0),ToolTransform.Origin,.0007f,copper);
        // Twin coolant feeds kept short to avoid an unmodelled swinging dress pack.
        for(int side=-1;side<=1;side+=2)
        {
            Segment(new(side*.024f,-.018f,.019f),new(side*.024f,.016f,.026f),.005f,graphite);
            Segment(new(side*.024f,-.023f,.019f),new(side*.024f,-.031f,.019f),.006f,side==1?blue:copper);
        }
    }
    private void Segment(Vector3 a,Vector3 b,float radius,Material material)
    {
        var mesh=StudioWorld.Cylinder(this,radius,a.DistanceTo(b),(a+b)*.5f,material,48);
        mesh.Quaternion=new Quaternion(Vector3.Up,(b-a).Normalized());
    }
    private void Ball(Vector3 point,float radius,Material material)=>AddChild(new MeshInstance3D {Position=point,Mesh=new SphereMesh {Radius=radius,Height=2*radius,RadialSegments=32,Rings=16},MaterialOverride=material});
}
