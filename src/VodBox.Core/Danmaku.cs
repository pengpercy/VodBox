namespace VodBox.Core;

public enum DanmakuMode { Scroll, Top, Bottom }
public sealed record DanmakuComment(long TimeMs, string Text, DanmakuMode Mode = DanmakuMode.Scroll, uint Color = 0xFFFFFF);
public sealed record DanmakuDocument
{
    public int Version { get; set; } = 1;
    public List<DanmakuComment> Comments { get; set; } = [];
}
public readonly record struct DanmakuPlacement(int Index, DanmakuComment Comment, double X, double Y, double Width);

/// <summary>Bounded, media-clock-driven lane scheduler. UI measurement is supplied by the renderer.</summary>
public sealed class DanmakuTimeline
{
    public const int DurationMs = 8000;
    public const int MaximumActive = 128;
    public const int MaximumCandidatesPerFrame = 256;
    private sealed record Active(int Index, DanmakuComment Comment, double Width, int Lane, double Y, double Speed);
    private readonly List<Active> _active = [];
    private readonly List<DanmakuPlacement> _placements = [];
    private IReadOnlyList<DanmakuComment> _comments = [];
    private int _cursor, _scrollLanes = 1, _fixedLanes = 1;
    private double _width, _height, _lineHeight = 32;
    private long _lastTime = -1;
    public IReadOnlyList<DanmakuPlacement> Placements => _placements;
    public void Configure(IReadOnlyList<DanmakuComment> comments, double width, double height, double fontSize, double coverage = .5)
    {
        _comments = comments; _width = Math.Max(0, width); _height = Math.Max(0, height); _lineHeight = Math.Clamp(fontSize, 16, 48) * 1.4;
        int rows = Math.Clamp((int)(Math.Max(0, _height - 2 * _lineHeight) / _lineHeight), 1, 24);
        _fixedLanes = rows >= 3 ? Math.Max(1, rows / 6) : 0;
        _scrollLanes = Math.Max(1, Math.Min(rows - 2 * _fixedLanes, (int)(rows * Math.Clamp(coverage, .25, 1)) - _fixedLanes));
        _lastTime = -1; _active.Clear(); _placements.Clear();
    }
    private Active? Tail(DanmakuMode mode, int lane)
    {
        for (int index = _active.Count - 1; index >= 0; index--) if (_active[index].Lane == lane && _active[index].Comment.Mode == mode) return _active[index];
        return null;
    }
    private int LowerBound(long time)
    {
        int left = 0, right = _comments.Count;
        while (left < right) { int middle = left + (right - left) / 2; if (_comments[middle].TimeMs < time) left = middle + 1; else right = middle; }
        return left;
    }
    private int UpperBound(long time, int left)
    {
        int right = _comments.Count;
        while (left < right) { int middle = left + (right - left) / 2; if (_comments[middle].TimeMs <= time) left = middle + 1; else right = middle; }
        return left;
    }
    public IReadOnlyList<DanmakuPlacement> Update(long timeMs, Func<DanmakuComment, double> measure)
    {
        timeMs = Math.Max(0, timeMs);
        if (_lastTime < 0 || timeMs < _lastTime || timeMs - _lastTime > 1000)
        { _active.Clear(); _cursor = LowerBound(Math.Max(0, timeMs - DurationMs + 1)); }
        _lastTime = timeMs;
        _active.RemoveAll(x => timeMs - x.Comment.TimeMs >= DurationMs);
        if (_width <= 0 || _height < _lineHeight) { _placements.Clear(); return _placements; }
        // Skip stale comments after a dense seek. At most 256 candidates are measured per frame.
        int start = _cursor; if (_cursor < _comments.Count && _comments[_cursor].TimeMs <= timeMs) _cursor = UpperBound(timeMs, _cursor);
        start = Math.Max(start, _cursor - MaximumCandidatesPerFrame);
        for (int index = start; index < _cursor; index++)
        {
            var comment = _comments[index];
            if (_active.Count >= MaximumActive || timeMs - comment.TimeMs >= DurationMs) continue;
            int lanes = comment.Mode == DanmakuMode.Scroll ? _scrollLanes : _fixedLanes;
            bool available = false;
            for (int lane = 0; lane < lanes; lane++)
            {
                var previous = Tail(comment.Mode, lane);
                double elapsed = previous is null ? DurationMs : comment.TimeMs - previous.Comment.TimeMs;
                if (previous is null || elapsed >= DurationMs || comment.Mode == DanmakuMode.Scroll && elapsed * previous.Speed - previous.Width >= 16) { available = true; break; }
            }
            if (!available) continue;
            double textWidth = measure(comment);
            if (!double.IsFinite(textWidth) || textWidth <= 0 || textWidth > _width * 4) continue;
            double speed = (_width + textWidth) / DurationMs;
            for (int lane = 0; lane < lanes; lane++)
            {
                var previous = Tail(comment.Mode, lane);
                if (previous is not null)
                {
                    // Use emission times to avoid overlapping comments when rebuilding a recent window.
                    double elapsed = comment.TimeMs - previous.Comment.TimeMs;
                    if (elapsed < DurationMs)
                    {
                        if (comment.Mode != DanmakuMode.Scroll) continue;
                        double gap = elapsed * previous.Speed - previous.Width;
                        double closing = Math.Max(0, speed - previous.Speed) * (DurationMs - elapsed);
                        if (gap < 16 + closing) continue;
                    }
                }
                double y = comment.Mode switch
                {
                    DanmakuMode.Top => lane * _lineHeight,
                    DanmakuMode.Bottom => Math.Max(0, _height - (lane + 2) * _lineHeight),
                    _ => (lane + _fixedLanes) * _lineHeight
                };
                _active.Add(new(index, comment, textWidth, lane, y, speed)); break;
            }
        }
        _placements.Clear();
        foreach (var item in _active)
        {
            double x = item.Comment.Mode == DanmakuMode.Scroll ? _width - (timeMs - item.Comment.TimeMs) * item.Speed : (_width - item.Width) / 2;
            _placements.Add(new(item.Index, item.Comment, x, item.Y, item.Width));
        }
        return _placements;
    }
}
