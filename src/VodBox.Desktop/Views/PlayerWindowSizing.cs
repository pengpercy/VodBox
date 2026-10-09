using Avalonia;

namespace VodBox.Desktop.Views;

/// <summary>按视频显示比例求窗口尺寸；先保比例，屏幕不足时放宽最小尺寸，绝不突破屏幕上限。</summary>
internal static class PlayerWindowSizing
{
    internal sealed record Limits(Size Size,Size Minimum,Size Maximum);
    internal static Limits Fit(double ratio,double availableWidth,double availableHeight,bool compact=false)
    {
        if(!double.IsFinite(ratio)||ratio<=0)throw new ArgumentOutOfRangeException(nameof(ratio));
        if(!double.IsFinite(availableWidth)||!double.IsFinite(availableHeight)||availableWidth<=0||availableHeight<=0)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        var maxWidth=Math.Min(compact?960:1920,availableWidth);
        var maxHeight=Math.Min(compact?720:1080,availableHeight);
        // 用高度作为统一尺度。竖屏和超宽媒体不套横屏的固定最小宽高。
        var maxScale=Math.Min(maxHeight,maxWidth/ratio);
        // 浮点乘法可能把上限算成1600.0000000000002；向内收一ULP保证不越屏。
        if(maxScale*ratio>maxWidth)maxScale=Math.BitDecrement(maxScale);
        var minScale=Math.Min(maxScale,Math.Max((compact?180:280)/ratio,compact?120:180));
        var targetScale=Math.Min(compact?360:720,(compact?640:1280)/ratio);
        var scale=Math.Clamp(targetScale,minScale,maxScale);
        var size=new Size(scale*ratio,scale);
        var minimum=new Size(minScale*ratio,minScale);
        // 控制条需要真实宽度：窗口最小宽度按控件需求抬高，但不突破屏幕与当前尺寸。
        var required=Math.Min(maxWidth,PlayerLayout.MinimumWindowWidth(compact));
        minimum=new Size(Math.Min(size.Width,Math.Max(minimum.Width,required)),minimum.Height);
        return new(size,minimum,new Size(maxScale*ratio,maxScale));
    }
}
