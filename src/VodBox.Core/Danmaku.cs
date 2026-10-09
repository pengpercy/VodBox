namespace VodBox.Core;

public enum DanmakuMode { Scroll, Top, Bottom }
public sealed record DanmakuComment(double Seconds, string Text, uint Color, DanmakuMode Mode);
