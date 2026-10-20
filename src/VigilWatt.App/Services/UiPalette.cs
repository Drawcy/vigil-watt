using Microsoft.UI.Xaml.Media;
using VigilWatt.Core.Models;
using Windows.UI;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 全局统一状态色板：设备列表、历史图表、桌面小组件、托盘图标共用同一组状态色值。
/// 2026/10/06 15:53:36 xliu
/// 状态色为明暗主题通用的固定值；中性色（文字/卡片底/分隔线）一律用 ThemeResource，不在这里定义。
/// </summary>
public static class UiPalette
{
    public static readonly Color Charging = FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Low = FromRgb(0xE2, 0x44, 0x5C);
    public static readonly Color Mid = FromRgb(0xFA, 0xAA, 0x17);
    public static readonly Color Good = FromRgb(0x34, 0xC7, 0x59);
    public static readonly Color Offline = FromRgb(0x8E, 0x8E, 0x93);

    // ---- 来源徽标色（设备列表 + 桌面小组件共用）----
    // 三种来源用三个互不相近的色相，一眼区分：蓝牙=蓝（蓝牙的通用色）/ 2.4G=紫 / 本机=青。
    // 与电量状态色刻意错开（状态色是红/琥珀/绿/蓝/灰，这里避开红绿琥珀，免得"是来源还是电量"分不清）。
    public static readonly Color SourceBluetooth = FromRgb(0x0A, 0x84, 0xFF);
    public static readonly Color Source24G = FromRgb(0xAF, 0x52, 0xDE);
    public static readonly Color SourceSystem = FromRgb(0x30, 0xB0, 0xC7);

    /// <summary>来源 → 徽标主色（文字色；胶囊底色用 <see cref="Tint"/> 压淡）。</summary>
    public static Color SourceTag(DeviceSource source) => source switch
    {
        DeviceSource.Ble or DeviceSource.BluetoothClassic => SourceBluetooth,
        DeviceSource.System => SourceSystem,
        _ => Source24G,
    };

    /// <summary>同色半透明底（徽标胶囊背景）。默认约 16% 不透明度——明暗主题下都够淡，不抢电量条。</summary>
    public static Color Tint(Color color, byte alpha = 0x28) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>多系列图表循环取色（首位为主系列色）。</summary>
    public static readonly Color[] SeriesColors =
    [
        Charging,
        Good,
        Mid,
        FromRgb(0xAF, 0x52, 0xDE),
        FromRgb(0x5E, 0xCE, 0xF1),
        FromRgb(0xFF, 0x9F, 0x0A),
    ];

    /// <summary>
    /// 设备自定义颜色的 16 色预设。选色对话框按 4×4 展示；
    /// 色相覆盖红/橙/黄/绿/青/蓝/紫/粉，明暗主题下都清晰可辨。
    /// </summary>
    public static readonly Color[] CustomColorChoices =
    [
        FromRgb(0x0A, 0x84, 0xFF), FromRgb(0x32, 0xAD, 0xE6), FromRgb(0x5E, 0xCE, 0xF1), FromRgb(0x30, 0xB0, 0xC7),
        FromRgb(0x34, 0xC7, 0x59), FromRgb(0xA3, 0xE6, 0x35), FromRgb(0xFF, 0xD6, 0x0A), FromRgb(0xFF, 0x9F, 0x0A),
        FromRgb(0xFF, 0x6B, 0x3D), FromRgb(0xE2, 0x44, 0x5C), FromRgb(0xFF, 0x2D, 0x55), FromRgb(0xBF, 0x5A, 0xF2),
        FromRgb(0xAF, 0x52, 0xDE), FromRgb(0x58, 0x56, 0xD6), FromRgb(0xA2, 0x84, 0x5E), FromRgb(0x8E, 0x8E, 0x93),
    ];

    /// <summary>把 <c>#RRGGBB</c> 解析成颜色；空值/格式不对返回 null（配置被手改坏时按"未设置"处理）。</summary>
    public static Color? TryParseHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6) return null;
        if (!byte.TryParse(s[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(s[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(s[4..], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return null;
        return Color.FromArgb(255, r, g, b);
    }

    /// <summary>颜色 → <c>#RRGGBB</c>（落盘用）。</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>电量状态 → 状态色。离线（IsOnline=false）由调用方直接用 Offline。</summary>
    public static Color BatteryLevel(int? percent, bool charging)
    {
        if (charging) return Charging;
        return percent switch
        {
            null => Offline,
            <= 20 => Low,
            <= 50 => Mid,
            _ => Good,
        };
    }

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>
    /// 取主题色资源（如 <c>TextFillColorPrimaryBrush</c>）的 RGB。找不到时回落到中灰，
    /// **不要用 Color.Black 兜底**：深色主题下会变成看不见的黑字。
    /// </summary>
    public static Color FromResource(string key)
    {
        if (Microsoft.UI.Xaml.Application.Current?.Resources.TryGetValue(key, out var value) == true
            && value is SolidColorBrush brush)
            return brush.Color;
        return FromRgb(0x20, 0x20, 0x20);
    }

    /// <summary>
    /// 设备强调色（名称 / 百分比 / 电量条共用，设备页与小组件同口径）：
    /// 离线灰 → 用户自定义色 → **主题前景色**（不满电不染绿/蓝，与任务栏贴片一致）。
    /// 三处展示面必须只走这一个入口，否则颜色会不一致。
    /// <para><paramref name="text"/> 是明暗主题下的前景色，由调用方从主题资源取（这里不能碰 XAML 资源）。</para>
    /// </summary>
    public static Color Accent(int? percent, bool charging, bool online, Color? custom, Color text)
    {
        if (!online) return Offline;
        if (custom is { } c) return c;
        // 低电是唯一需要"染色"的状态（≤20% 红），其余一律中性——贴片一直是这个规则
        return percent is <= 20 ? Low : text;
    }

    /// <summary>托盘图标走 GDI+，需要 System.Drawing 的颜色类型。</summary>
    public static System.Drawing.Color ToDrawing(Color color) =>
        System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
}
