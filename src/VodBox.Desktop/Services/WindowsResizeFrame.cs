using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Controls.Platform;

namespace VodBox.Desktop.Services;

/// <summary>
/// Windows：扩展客户区后，Avalonia 12 把可调整大小窗口左/右/下整条 8px（100% 缩放）调整框都留作非客户区，
/// 但其中只有一部分是可见边框，剩下的在内容和窗口边框之间露出一条缝，关闭按钮的红色悬停底色也到不了窗口边缘。
/// 这里在非最大化时把客户区边缘拉到可见边框处；调整大小仍由 Avalonia 的命中测试负责。固定大小的窗口边框很细，没有这条缝。
/// </summary>
internal static class WindowsResizeFrame
{
    private const uint WM_NCCALCSIZE = 0x0083;
    private const int GWL_STYLE = -16;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const int SM_CXBORDER = 5;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    public static void Attach(Window window)
    {
        // 下面按 DPI 取度量的 API 从 Windows 10 1607 起才有
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393)) return;
        Win32Properties.AddWndProcHookCallback(window, Hook);
        // 显示后重算一次边框，第一次布局就用上更宽的客户区。
        window.Opened += (_, _) =>
        {
            if (window.TryGetPlatformHandle()?.Handle is { } hWnd)
                SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        };
    }

    private static IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCCALCSIZE || wParam == IntPtr.Zero || IsZoomed(hWnd) || IsIconic(hWnd)) return IntPtr.Zero;

        var style = (uint)GetWindowLongW(hWnd, GWL_STYLE);
        if ((style & WS_THICKFRAME) == 0 || (style & WS_CAPTION) != WS_CAPTION) return IntPtr.Zero;

        // 客户区边缘对齐 DWM 报告的可见边框（200% 时内缩 10px，而 Avalonia 留了 13px）；
        // DWM 还不知道边框时，退回到调整框减去 1px 边线。
        int left, right, bottom;
        if (GetWindowRect(hWnd, out var window) &&
            DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var visible, Marshal.SizeOf<RECT>()) == 0 &&
            visible.Left > window.Left && visible.Right < window.Right && visible.Bottom < window.Bottom)
        {
            (left, right, bottom) = (visible.Left - window.Left, window.Right - visible.Right, window.Bottom - visible.Bottom);
        }
        else
        {
            var dpi = GetDpiForWindow(hWnd);
            var frame = new RECT();
            if (dpi == 0 || !AdjustWindowRectExForDpi(ref frame, style, false, 0, dpi)) return IntPtr.Zero;
            var border = Math.Max(1, GetSystemMetricsForDpi(SM_CXBORDER, dpi));
            (left, right, bottom) = (-frame.Left - border, frame.Right - border, frame.Bottom - border);
        }
        if (left < 0 || right < 0 || bottom < 0) return IntPtr.Zero;

        // rgrc[0] 是 NCCALCSIZE_PARAMS 的第一个字段：建议的窗口矩形，处理后即客户区矩形。
        // 顶边保持在窗口边缘，与 Avalonia 扩展标题栏时的做法一致。
        var rect = Marshal.PtrToStructure<RECT>(lParam);
        rect.Left += left;
        rect.Right -= right;
        rect.Bottom -= bottom;
        Marshal.StructureToPtr(rect, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustWindowRectExForDpi(ref RECT rect, uint style,
        [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
