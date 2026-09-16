using Godot;
using System;

namespace EstunStudio;

/// <summary>SwissCAM-style circular viewport tools, drawn crisply at native resolution.</summary>
public partial class RoundIconButton : Button
{
    public string Glyph { get; set; } = "axes";
    public bool Alert { get; set; }
    public RoundIconButton()
    {
        CustomMinimumSize=Size=new Vector2(36,36);FocusMode=FocusModeEnum.None;
        MouseDefaultCursorShape=CursorShape.PointingHand;
        foreach(string state in new[]{"normal","hover","pressed","disabled","focus"})AddThemeStyleboxOverride(state,new StyleBoxEmpty());
        MouseEntered+=QueueRedraw;MouseExited+=QueueRedraw;Toggled+=_=>QueueRedraw();
    }
    public override void _Draw()
    {
        Vector2 c=Size*.5f;bool active=ButtonPressed||IsHovered();
        DrawCircle(c,17,Alert?new Color("62283b"):active?new Color("284a49"):new Color("26333c"));
        DrawArc(c,17,0,Mathf.Tau,48,active?new Color("60dabe"):new Color("4b5c67"),1,true);
        Color ink=Alert?new Color("ff7086"):active?new Color("b3ffee"):new Color("d2dde3");
        void L(float x,float y,float a,float b)=>DrawLine(c+new Vector2(x,y),c+new Vector2(a,b),ink,1.5f,true);
        void Circle(float radius)=>DrawArc(c,radius,0,Mathf.Tau,36,ink,1.5f,true);
        switch(Glyph)
        {
            case "gizmo": L(-7,5,7,5);L(-7,5,-7,-7);L(-7,5,2,-4);DrawRect(new Rect2(c+new Vector2(1,-8),new Vector2(7,7)),ink,false,1.2f);break;
            case "axes": L(-6,6,-6,-7);L(-6,6,8,6);L(-6,6,4,-4);L(-8,-4,-6,-7);L(-6,-7,-3,-4);break;
            case "trace": DrawPolyline(new[]{c+new Vector2(-8,5),c+new Vector2(-4,-5),c+new Vector2(1,3),c+new Vector2(7,-7)},ink,1.7f,true);break;
            case "collision": DrawPolyline(new[]{c+new Vector2(0,-9),c+new Vector2(9,7),c+new Vector2(-9,7),c+new Vector2(0,-9)},ink,1.4f,true);L(0,-3,0,2);DrawCircle(c+new Vector2(0,5),1,ink);break;
            case "shadows": Circle(8);DrawArc(c,5,Mathf.Pi/2,Mathf.Pi*1.5f,16,ink,5);break;
            case "ao": Circle(8);Circle(4);break;
            case "ssil": Circle(4);L(-9,0,-6,0);L(6,0,9,0);L(0,-9,0,-6);L(0,6,0,9);break;
            case "ssr": L(-8,5,8,5);L(-6,-7,0,2);L(0,2,6,-7);L(-6,9,6,9);break;
            case "fog": L(-8,-5,8,-5);L(-5,0,9,0);L(-9,5,5,5);break;
            case "bloom": case "exposure": Circle(5);for(int i=0;i<8;i++){float a=i*Mathf.Tau/8;DrawLine(c+Vector2.FromAngle(a)*7,c+Vector2.FromAngle(a)*10,ink,1.4f,true);}break;
            case "sparks": L(-9,0,9,0);L(0,-9,0,9);L(-6,-6,6,6);L(-6,6,6,-6);break;
            case "smoke": DrawArc(c+new Vector2(-3,1),5,Mathf.Pi*.6f,Mathf.Pi*1.8f,16,ink,1.5f,true);DrawArc(c+new Vector2(3,-2),5,-Mathf.Pi*.5f,Mathf.Pi*.7f,16,ink,1.5f,true);break;
            case "beads": for(int i=-1;i<=1;i++)DrawArc(c+new Vector2(i*5,0),4,-Mathf.Pi*.5f,Mathf.Pi*.5f,12,ink,1.5f,true);break;
            case "resolution": DrawRect(new Rect2(c-new Vector2(9,6),new Vector2(18,12)),ink,false,1.4f);L(-4,9,4,9);break;
            case "msaa": L(-7,7,-7,1);L(-7,1,-1,1);L(-1,1,-1,-6);L(-1,-6,7,-6);break;
            case "taa": Circle(7);L(-4,-4,4,4);L(-4,4,4,-4);break;
            case "dof": Circle(8);Circle(2);break;
            case "reset": DrawArc(c,7,-Mathf.Pi*.5f,Mathf.Pi*1.2f,24,ink,1.5f,true);L(-8,-4,-7,0);L(-7,0,-3,-2);break;
            default: Circle(6);break;
        }
    }
}
