using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VodBox.Core;
using VodBox.Desktop.ViewModels;

namespace VodBox.Desktop.Views;

/// <summary>按媒体时钟绘制弹幕；暂停/seek无需独立动画时钟，关闭或换集立即清空。</summary>
public sealed class DanmakuLayer : Control
{
    internal static IReadOnlyList<DanmakuComment> ActiveAt(IReadOnlyList<DanmakuComment> comments, double seconds,int limit=60)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return [];
        var low = 0; var high = comments.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (comments[middle].Seconds <= seconds - 8) low = middle + 1; else high = middle;
        }
        var active = new List<DanmakuComment>(Math.Clamp(limit,1,60));
        for (var index = low; index < comments.Count && active.Count < Math.Clamp(limit,1,60) && comments[index].Seconds <= seconds; index++) active.Add(comments[index]);
        return active;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (DataContext is not PlayerViewModel vm || !vm.DanmakuEnabled || !vm.Visible) return;
        var seconds = vm.Position.TotalSeconds;
        var active = ActiveAt(vm.Danmaku, seconds,vm.DanmakuLimit);
        var prepared=active.Select(item=>
        {
            var color=Color.FromArgb((byte)(vm.DanmakuOpacity*255),(byte)(item.Color>>16),(byte)(item.Color>>8),(byte)item.Color);
            var text=new FormattedText(item.Text,System.Globalization.CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(FontFamily.Default),20,new SolidColorBrush(color));
            return(item,text);
        }).ToArray();
        var placed=Place(prepared.Select(pair=>(pair.item,pair.text.Width)).ToArray(),seconds,Bounds.Width,Bounds.Height);
        foreach(var placement in placed)context.DrawText(prepared[placement.Index].text,new Point(placement.X,placement.Y));
    }

    internal sealed record Placement(int Index,double X,double Y,double Width);
    internal static IReadOnlyList<Placement> Place(IReadOnlyList<(DanmakuComment Comment,double Width)> items,double seconds,double width,double height)
    {
        var lanes=Math.Max(1,Math.Min(12,(int)((height-48)/28)));
        var occupied=new List<(double X,double Y,double Width)>();var result=new List<Placement>();
        for(var index=0;index<items.Count;index++)
        {
            var (item,textWidth)=items[index];var elapsed=seconds-item.Seconds;
            if(elapsed<0||elapsed>=8)continue;
            var x=item.Mode==DanmakuMode.Scroll?width-(width+textWidth)*elapsed/8:(width-textWidth)/2;
            for(var lane=0;lane<lanes;lane++)
            {
                var y=item.Mode==DanmakuMode.Bottom?height-32-lane*28:16+lane*28;
                if(occupied.Any(other=>Math.Abs(other.Y-y)<28&&x<other.X+other.Width+12&&x+textWidth+12>other.X))continue;
                occupied.Add((x,y,textWidth));result.Add(new Placement(index,x,y,textWidth));break;
            }
        }
        return result;
    }
}
