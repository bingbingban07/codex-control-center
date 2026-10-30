namespace CodexControlCenter.Core;

public sealed record WallpaperFrame(double Left,double Top,double Width,double Height);

/// <summary>Image bounds in viewport pixels; the renderer uses the same formula in its own CSS coordinate space.</summary>
public static class WallpaperComposition
{
    public static WallpaperFrame Calculate(double imageWidth,double imageHeight,double width,double height,WallpaperSettings settings)
    {
        WallpaperLibrary.Validate(settings);
        if(!double.IsFinite(imageWidth)||!double.IsFinite(imageHeight)||!double.IsFinite(width)||!double.IsFinite(height)||imageWidth<=0||imageHeight<=0||width<=0||height<=0)
            throw new ArgumentOutOfRangeException(nameof(width),"图片与预览尺寸必须为正数。");
        double scale=(settings.Fit.Equals("contain",StringComparison.OrdinalIgnoreCase)?Math.Min(width/imageWidth,height/imageHeight):Math.Max(width/imageWidth,height/imageHeight))*settings.Zoom;
        if(settings.Blur>0&&settings.Fit.Equals("cover",StringComparison.OrdinalIgnoreCase))scale*=1+settings.Blur*4/675;
        double renderedWidth=imageWidth*scale,renderedHeight=imageHeight*scale;
        return new((width-renderedWidth)*settings.PositionX/100,(height-renderedHeight)*settings.PositionY/100,renderedWidth,renderedHeight);
    }
}
