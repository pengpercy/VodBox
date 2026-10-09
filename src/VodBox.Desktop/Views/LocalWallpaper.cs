using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace VodBox.Desktop.Views;

/// <summary>本地壁纸；有界读取并缩放解码，卸载释放图像。</summary>
public sealed class LocalWallpaper:Image
{
    public static readonly StyledProperty<string?> PathProperty=AvaloniaProperty.Register<LocalWallpaper,string?>(nameof(Path));
    public string? Path{get=>GetValue(PathProperty);set=>SetValue(PathProperty,value);}
    private Bitmap? _image;
    private long _generation;
    internal Task LoadingTask{get;private set;}=Task.CompletedTask;
    private bool _attached;
    private void Reload()=>LoadingTask=LoadAsync();
    public LocalWallpaper()
    {
        PropertyChanged+=(_,e)=>{if(e.Property==PathProperty&&_attached)Reload();};
        AttachedToVisualTree+=(_,_)=>{_attached=true;Reload();};
        DetachedFromVisualTree+=(_,_)=>{_attached=false;++_generation;Source=null;_image?.Dispose();_image=null;};
    }
    private async Task LoadAsync()
    {
        var generation=++_generation;var path=Path;Bitmap? bitmap=null;
        try
        {
            if(!string.IsNullOrWhiteSpace(path)&&System.IO.Path.IsPathFullyQualified(path)&&File.Exists(path))
            {
                if(new FileInfo(path).Length>16*1024*1024)throw new InvalidDataException("壁纸文件过大。");
                bitmap=await Task.Run(()=>{using var input=File.OpenRead(path);return Bitmap.DecodeToWidth(input,1920);});
            }
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()=>
            {
                if(generation!=_generation)return;
                var old=_image;_image=bitmap;Source=bitmap;bitmap=null;old?.Dispose();
            });
        }
        catch(Exception error)when(error is not OutOfMemoryException){System.Diagnostics.Debug.WriteLine($"[wallpaper] {error.Message}");}
        finally{bitmap?.Dispose();}
    }
}
