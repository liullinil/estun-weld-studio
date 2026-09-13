using Godot;
using System;

namespace EstunStudio;

/// <summary>Keep the 3D pixel workload stable when a window grows; canvas/UI remain at native resolution.</summary>
public static class RenderResolution
{
    public const int DefaultPixelBudget = 1440 * 900;
    public const int Auto = 0;
    public const int Native = 1;
    public const int Manual = 2;
    public const float MinimumScale = .25f;

    public static float CalculateScale(Vector2I renderSize, int mode, float manualScale = 1)
    {
        if(renderSize.X<=0 || renderSize.Y<=0)return 1;
        if(mode==Native)return 1;
        if(mode==Manual)return float.IsFinite(manualScale)?Mathf.Clamp(manualScale,MinimumScale,1):1;
        double pixels=(double)renderSize.X*renderSize.Y;
        return (float)Math.Clamp(Math.Sqrt(DefaultPixelBudget/pixels),MinimumScale,1);
    }

    public static Vector2I GetRenderSize(Viewport viewport)
    {
        // With canvas_items, GetVisibleRect alone is the logical design size.
        // Stretch maps it to the physical render surface, excluding black bars.
        Vector2 logical=viewport.GetVisibleRect().Size;
        Vector2 stretch=viewport.GetStretchTransform().Scale.Abs();
        Vector2 physical=logical*stretch;
        if(!physical.IsFinite())return Vector2I.One;
        return new Vector2I(Math.Max(1,(int)Math.Round(physical.X)),Math.Max(1,(int)Math.Round(physical.Y)));
    }

    public static void Apply(Viewport viewport, RenderSettings settings)
    {
        float scale=CalculateScale(GetRenderSize(viewport),settings.ResolutionMode,settings.ManualRenderScale);
        var mode=scale<.9999f?Viewport.Scaling3DModeEnum.Fsr:Viewport.Scaling3DModeEnum.Bilinear;
        if(viewport.Scaling3DMode!=mode)viewport.Scaling3DMode=mode;
        if(!Mathf.IsEqualApprox(viewport.Scaling3DScale,scale))viewport.Scaling3DScale=scale;
        viewport.FsrSharpness=.2f;
    }
}
