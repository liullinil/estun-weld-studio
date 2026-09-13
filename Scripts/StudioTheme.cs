using Godot;
namespace EstunStudio;

/// <summary>Native-pixel UI tokens shared by the docks and application chrome.</summary>
public static class StudioTheme
{
    public static readonly Color Background=new("151a1f"),Panel=new("20272e"),Border=new("35414a"),Text=new("e7edf0"),Muted=new("96a4ae"),Accent=new("e4b46c");
    public static StyleBoxFlat Box(Color color,int radius=6,bool border=false)=>new()
    {
        BgColor=color,CornerRadiusTopLeft=radius,CornerRadiusTopRight=radius,CornerRadiusBottomLeft=radius,CornerRadiusBottomRight=radius,
        BorderColor=Border,BorderWidthBottom=border?1:0,BorderWidthLeft=border?1:0,BorderWidthRight=border?1:0,BorderWidthTop=border?1:0
    };
    public static Theme Create()
    {
        var font=GD.Load<FontFile>("res://Assets/Fonts/Inter-Regular.ttf");
        font.Hinting=TextServer.Hinting.Normal;
        font.SubpixelPositioning=TextServer.SubpixelPositioning.Disabled;
        var theme=new Theme {DefaultFont=font,DefaultFontSize=14};
        theme.SetColor("font_color","Label",Text);theme.SetColor("font_color","Button",Text);
        theme.SetColor("font_hover_color","Button",Colors.White);theme.SetColor("font_pressed_color","Button",Accent);
        foreach(string state in new[]{"normal","hover","pressed","disabled"})
        {
            var style=Box(new Color(state=="hover"?"36444f":state=="pressed"?"424236":"2a343d"),5,true);
            style.ContentMarginLeft=12;style.ContentMarginRight=12;style.ContentMarginTop=8;style.ContentMarginBottom=8;
            theme.SetStylebox(state,"Button",style);
        }
        var edit=Box(new Color("161e24"),4,true);edit.ContentMarginLeft=8;edit.ContentMarginRight=8;edit.ContentMarginTop=6;edit.ContentMarginBottom=6;
        theme.SetStylebox("normal","LineEdit",edit);theme.SetColor("font_color","LineEdit",Text);
        theme.SetColor("font_color","CheckBox",Text);theme.SetFontSize("font_size","TooltipLabel",13);
        theme.SetStylebox("panel","Panel",Box(Panel,8,true));
        theme.SetStylebox("panel","PanelContainer",Box(Panel,8,true));
        theme.SetConstant("separation","HBoxContainer",8);theme.SetConstant("separation","VBoxContainer",8);
        return theme;
    }
    public static Label Label(string text,int size=14,Color? color=null)=>new()
    {
        Text=text,MouseFilter=Control.MouseFilterEnum.Ignore,VerticalAlignment=VerticalAlignment.Center,
        Theme=new Theme{DefaultFontSize=size},Modulate=color??Text
    };
}
