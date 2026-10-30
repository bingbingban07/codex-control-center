using CodexControlCenter.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace CodexControlCenter.App;

internal static class WallpaperPreview
{
    private sealed record Decoded(string Fingerprint,byte[] Pixels,int Width,int Height);
    private static readonly SemaphoreSlim Gate=new(1);
    private static readonly LinkedList<Decoded> Cache=new();
    public static async Task<SoftwareBitmap> RenderAsync(string path,WallpaperSettings settings,bool dark,int width=1200,int height=675,CancellationToken cancellationToken=default)
    {
        if(width is <100 or >2000||height is <100 or >2000)throw new ArgumentOutOfRangeException(nameof(width));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try{
            var info=new FileInfo(path);string fingerprint=info.FullName+":"+info.Length+":"+info.LastWriteTimeUtc.Ticks;
            var decoded=Cache.FirstOrDefault(c=>c.Fingerprint==fingerprint);
            if(decoded is null){
                using var file=await (await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken).ConfigureAwait(false)).OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
                var decoder=await BitmapDecoder.CreateAsync(file).AsTask(cancellationToken).ConfigureAwait(false);
                double factor=Math.Min(1,1600.0/Math.Max(decoder.PixelWidth,decoder.PixelHeight));
                uint sw=Math.Max(1,(uint)(decoder.PixelWidth*factor)),sh=Math.Max(1,(uint)(decoder.PixelHeight*factor));
                var pixels=await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,new BitmapTransform{ScaledWidth=sw,ScaledHeight=sh,InterpolationMode=BitmapInterpolationMode.Fant},ExifOrientationMode.RespectExifOrientation,ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
                if(decoder.PixelWidth!=decoder.PixelHeight&&decoder.OrientedPixelWidth==decoder.PixelHeight&&decoder.OrientedPixelHeight==decoder.PixelWidth)(sw,sh)=(sh,sw);
                decoded=new(fingerprint,pixels.DetachPixelData(),(int)sw,(int)sh);Cache.AddFirst(decoded);while(Cache.Count>2)Cache.RemoveLast();
            }
            var source=decoded;byte[] result=await Task.Run(()=>Compose(source,settings,dark,width,height,cancellationToken),cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();using var writer=new DataWriter();writer.WriteBytes(result);
            return SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),BitmapPixelFormat.Bgra8,width,height,BitmapAlphaMode.Premultiplied);
        }finally{Gate.Release();}
    }
    private static byte[] Compose(Decoded image,WallpaperSettings settings,bool dark,int width,int height,CancellationToken ct)
    {
        byte[] raster=new byte[width*height*4];var bounds=WallpaperComposition.Calculate(image.Width,image.Height,width,height,settings);byte background=dark?(byte)24:(byte)255;
        double sxScale=image.Width/bounds.Width,syScale=image.Height/bounds.Height;
        for(int y=0;y<height;y++){
            ct.ThrowIfCancellationRequested();double sy=(y-bounds.Top)*syScale;
            for(int x=0;x<width;x++){
                double sx=(x-bounds.Left)*sxScale;int dest=(y*width+x)*4;bool inside=sx>=0&&sy>=0&&sx<image.Width&&sy<image.Height;
                for(int c=0;c<3;c++){
                    if(!inside){raster[dest+c]=background;continue;}
                    int ix=(int)sx,iy=(int)sy,pixel=(iy*image.Width+ix)*4;
                    raster[dest+c]=(byte)Math.Clamp(image.Pixels[pixel+c]+background*(1-image.Pixels[pixel+3]/255.0),0,255);
                }
                raster[dest+3]=255;
            }
        }
        int radius=(int)Math.Round(settings.Blur*Math.Min(width,height)/675*.58);if(radius>0)for(int pass=0;pass<3;pass++)raster=Blur(raster,width,height,radius,ct);
        double brightness=1-settings.Dimming;
        for(int y=0;y<height;y++){ct.ThrowIfCancellationRequested();for(int i=y*width*4;i<(y+1)*width*4;i+=4)for(int c=0;c<3;c++)raster[i+c]=settings.Enabled?(byte)Math.Clamp(Math.Round(raster[i+c]*brightness),0,255):background;}
        return raster;
    }
    private static byte[] Blur(byte[] source,int width,int height,int radius,CancellationToken ct)
    {
        byte[] temp=new byte[source.Length],dest=new byte[source.Length];int diameter=radius*2+1;
        for(int y=0;y<height;y++){
            ct.ThrowIfCancellationRequested();for(int c=0;c<4;c++){
                int sum=0;for(int k=-radius;k<=radius;k++)sum+=source[(y*width+Math.Clamp(k,0,width-1))*4+c];
                for(int x=0;x<width;x++){temp[(y*width+x)*4+c]=(byte)(sum/diameter);sum+=source[(y*width+Math.Clamp(x+radius+1,0,width-1))*4+c]-source[(y*width+Math.Clamp(x-radius,0,width-1))*4+c];}
            }
        }
        for(int x=0;x<width;x++){
            ct.ThrowIfCancellationRequested();for(int c=0;c<4;c++){
                int sum=0;for(int k=-radius;k<=radius;k++)sum+=temp[(Math.Clamp(k,0,height-1)*width+x)*4+c];
                for(int y=0;y<height;y++){dest[(y*width+x)*4+c]=(byte)(sum/diameter);sum+=temp[(Math.Clamp(y+radius+1,0,height-1)*width+x)*4+c]-temp[(Math.Clamp(y-radius,0,height-1)*width+x)*4+c];}
            }
        }
        return dest;
    }
}
