namespace VodBox.Application;

/// <summary>Try each configured mirror once; manual selection begins a fresh retry cycle.</summary>
public sealed class LiveRetryPolicy
{
    private bool[] _tried = [];
    public int Current { get; private set; }
    public void Reset(int count, int selected = 0)
    {
        if (count < 1 || selected < 0 || selected >= count) throw new ArgumentOutOfRangeException(nameof(count));
        _tried = new bool[count]; Current = selected; _tried[selected] = true;
    }
    public bool TryAdvance(out int mirror)
    {
        for (int offset = 1; offset < _tried.Length; offset++)
        {
            int next = (Current + offset) % _tried.Length;
            if (_tried[next]) continue;
            _tried[next] = true; Current = next; mirror = next; return true;
        }
        mirror = Current; return false;
    }
}
