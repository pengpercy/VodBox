package android.graphics;

import java.util.Locale;

/**
 * Android {@code android.graphics.Color} 最小子集（VodBox 自有实现，独立编写）。
 *
 * 纯整数运算 + 颜色名解析，**语义完全可实现**：{@code ARGB} 打包/拆包与 Android 一致，
 * {@link #parseColor(String)} 只接受 Android 支持的 {@code #RRGGBB}/{@code #AARRGGBB}
 * 与命名颜色表（**不**额外接受 {@code #RGB} 这类 Web 简写）。宿主不渲染 UI，但插件常用它做
 * 字符串→颜色的存取（写进偏好/JSON）。
 *
 * 未实现（调用即 NoSuchMethodError）：{@code HSVToColor}/{@code colorToHSV}、
 * {@code rgb(long,long,long)}（API 26 的 long 重载）、{@code valueOf}/{@code alpha}/{@code red}
 * 的 float 变体。
 */
public final class Color {

    public static final int BLACK = 0xFF000000;
    public static final int DKGRAY = 0xFF444444;
    public static final int GRAY = 0xFF888888;
    public static final int LTGRAY = 0xFFCCCCCC;
    public static final int WHITE = 0xFFFFFFFF;
    public static final int RED = 0xFFFF0000;
    public static final int GREEN = 0xFF00FF00;
    public static final int BLUE = 0xFF0000FF;
    public static final int YELLOW = 0xFFFFFF00;
    public static final int CYAN = 0xFF00FFFF;
    public static final int MAGENTA = 0xFFFF00FF;
    public static final int TRANSPARENT = 0;

    private Color() {
    }

    public static int alpha(int color) {
        return color >>> 24;
    }

    public static int red(int color) {
        return (color >> 16) & 0xFF;
    }

    public static int green(int color) {
        return (color >> 8) & 0xFF;
    }

    public static int blue(int color) {
        return color & 0xFF;
    }

    public static int rgb(int red, int green, int blue) {
        return 0xFF000000 | (red << 16) | (green << 8) | blue;
    }

    public static int argb(int alpha, int red, int green, int blue) {
        return (alpha << 24) | (red << 16) | (green << 8) | blue;
    }

    /** 与 Android 一致：只接受 {@code #RRGGBB} / {@code #AARRGGBB} 十六进制或命名颜色表里的名字。 */
    public static int parseColor(String colorString) {
        if (colorString == null) throw new IllegalArgumentException("Unknown color");
        if (colorString.startsWith("#")) {
            long value = Long.parseLong(colorString.substring(1), 16);
            if (colorString.length() == 7) return (int) (value | 0xFF000000L);
            if (colorString.length() == 9) return (int) value;
            throw new IllegalArgumentException("Unknown color");
        }
        switch (colorString.toLowerCase(Locale.ROOT)) {
            case "black": return BLACK;
            case "darkgray":
            case "darkgrey": return DKGRAY;
            case "gray":
            case "grey": return GRAY;
            case "lightgray":
            case "lightgrey": return LTGRAY;
            case "white": return WHITE;
            case "red": return RED;
            case "green": return GREEN;
            case "blue": return BLUE;
            case "yellow": return YELLOW;
            case "cyan": return CYAN;
            case "magenta": return MAGENTA;
            case "aqua": return CYAN;
            case "fuchsia": return MAGENTA;
            case "lime": return GREEN;
            case "maroon": return 0xFF800000;
            case "navy": return 0xFF000080;
            case "olive": return 0xFF808000;
            case "purple": return 0xFF800080;
            case "silver": return 0xFFC0C0C0;
            case "teal": return 0xFF008080;
            default: throw new IllegalArgumentException("Unknown color");
        }
    }
}
