using VigilWatt.Core.Models;
using Gdi = System.Drawing;
using Gdi2 = System.Drawing.Drawing2D;
using GdiImaging = System.Drawing.Imaging;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏贴片的绘制：把"每台设备一格"画进一张 32 位预乘 ARGB 位图，交给原生分层窗口提交 （贴片是 <c>SetParent</c> 进任务栏的**原生子窗口**，用不了 XAML，原理见 runtime-pitfalls.md §16）。
/// 2026/10/12 17:20:14 xliu
/// **不画底色与边框**——贴片是浮在任务栏上的一行读数，加了底框就会糊出一个突兀的方块。
/// 多设备时按两行均摊（少的那行水平居中），行高压到原来一半，详见 <see cref="Layout"/>。
/// </summary>
public static class TaskbarPillRenderer
{
    /// <summary>贴片左右内边距（DIP）。</summary>
    public const double PadH = 6;

    /// <summary>两行布局的分割阈值：1 台单行，≥2 台两行。</summary>
    private const int TwoRowThreshold = 2;

    private const double IconBox = 19;
    private const double IconTextGap = 4;
    private const double PercentFontSize = 15.5;
    private const double EmptyFontSize = 12;
    private const double EmptyTextWidthDip = 104;

    /// <summary>两行模式下的尺寸：行高减半，图标与字号跟着缩，否则塞不下。</summary>
    private const double RowIconBox = 14.5;
    private const double RowPercentFontSize = 12.5;
    private const double RowIconTextGap = 3;
    /// <summary>同一行内相邻两格的间距。</summary>
    private const double RowChipGap = 8;
    /// <summary>两行之间的垂直间隙（越小越紧凑）。</summary>
    private const double RowGapV = 0;

    /// <summary>宽度余量（像素）：字宽量出来差一两个像素就会把最右一格挤出窗口（历史事故）。</summary>
    private const int SlackPx = 8;

    private static readonly Gdi.Bitmap MeasureBmp = new(1, 1, GdiImaging.PixelFormat.Format32bppPArgb);
    private static readonly Gdi.Graphics MeasureG = Gdi.Graphics.FromImage(MeasureBmp);

    /// <summary>贴片前景配色。**没有底色/边框**，只有前景三项。IconStroke 与文字同色，图标更醒目。</summary>
    public readonly record struct Skin(Gdi.Color Divider, Gdi.Color IconStroke, Gdi.Color Text);

    public static Skin SkinFor(bool light) => light
        ? new Skin(
            Divider: Gdi.Color.FromArgb(0x33, 0x00, 0x00, 0x00),
            IconStroke: Gdi.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F),
            Text: Gdi.Color.FromArgb(0xFF, 0x1F, 0x1F, 0x1F))
        : new Skin(
            Divider: Gdi.Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF),
            IconStroke: Gdi.Color.FromArgb(0xFF, 0xFF, 0xFF),
            Text: Gdi.Color.FromArgb(0xFF, 0xFF, 0xFF));

    /// <summary>悬停命中一格：客户区 X/宽与提示文案同源自绘制，避免另算宽度偏一两个像素。</summary>
    public readonly record struct HitChip(string Id, string Tip, int X, int Width);

    /// <summary>折叠格（+N）的稳定 Id，重绘后仍能对上同一悬停目标。</summary>
    public const string OverflowHitId = ":overflow:";

    /// <summary>画一整条贴片，返回位图；<paramref name="widthPx"/> 是裁掉右侧空白后的实际宽度。</summary>
    /// <param name="accentColors">设备 Id → 用户自定义颜色；命中则百分比用它，否则按电量状态色。</param>
    /// <param name="lowBatteryThreshold">低电量阈值（%），≤ 该值时图标内部填红色。0 = 不填红色。</param>
    public static Gdi.Bitmap Render(
        IReadOnlyList<DeviceStatus> devices, IReadOnlyList<string> overflowNames,
        Skin skin, int heightPx, float scale, IReadOnlyDictionary<string, Gdi.Color>? accentColors,
        int lowBatteryThreshold,
        out int widthPx, out IReadOnlyList<HitChip> hits)
    {
        using var percentFont = new Gdi.Font("Microsoft YaHei UI", (float)(PercentFontSize * scale), Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
        using var rowFont = new Gdi.Font("Microsoft YaHei UI", (float)(RowPercentFontSize * scale), Gdi.FontStyle.Bold, Gdi.GraphicsUnit.Pixel);
        using var plainFont = new Gdi.Font("Microsoft YaHei UI", (float)(EmptyFontSize * scale), Gdi.FontStyle.Regular, Gdi.GraphicsUnit.Pixel);
        using var sf = (Gdi.StringFormat)Gdi.StringFormat.GenericTypographic.Clone();
        sf.FormatFlags |= Gdi.StringFormatFlags.NoWrap | Gdi.StringFormatFlags.MeasureTrailingSpaces;
        sf.LineAlignment = Gdi.StringAlignment.Center;

        // 单行或两行：把所有格子的几何一次性算出来，绘制只按结果画，不再算尺寸
        var chips = Layout(devices, overflowNames, heightPx, scale, percentFont, rowFont, plainFont, sf,
            out float totalW, out var overflowChip);

        int width = (int)Math.Ceiling(totalW + PadH * 2 * scale) + SlackPx;
        var bmp = new Gdi.Bitmap(width, heightPx, GdiImaging.PixelFormat.Format32bppPArgb);

        using (var g = Gdi.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Gdi2.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Gdi.Color.Transparent);
            using (var hit = new Gdi.SolidBrush(Gdi.Color.FromArgb(1, 0, 0, 0)))
                g.FillRectangle(hit, 0, 0, width, heightPx);

            float padLeft = (float)(PadH * scale);
            if (devices.Count == 0)
            {
                DrawText(g, "暂无在线设备", plainFont, Fade(skin.Text, 0.6f),
                    new Gdi.RectangleF(padLeft, 0, (float)(EmptyTextWidthDip * scale), heightPx), sf);
            }
            else
            {
                var fontForChip = devices.Count >= TwoRowThreshold ? rowFont : percentFont;
                foreach (var chip in chips)
                    DrawChipAt(g, chip, skin, padLeft, scale, fontForChip, sf,
                        AccentFor(chip.Device, accentColors, skin), lowBatteryThreshold);
                if (overflowChip != null)
                {
                    var ov = overflowChip.Value;
                    DrawText(g, $"+{ov.Overflow}", fontForChip, Fade(skin.Text, 0.6f),
                        new Gdi.RectangleF(padLeft + ov.X, ov.Y, ov.Width + 2, ov.Height), sf);
                }
            }
        }

        widthPx = Math.Min(width, ContentRight(bmp) + (int)Math.Ceiling(PadH * scale));
        hits = BuildHits(chips, overflowChip, scale);
        return bmp;
    }

    /// <summary>一格设备的几何：客户区相对位置（不含左 pad）与要显示的设备。</summary>
    private readonly record struct ChipGeometry(DeviceStatus Device, float X, float Y, float Width, float Height);

    /// <summary>+N 折叠格的几何。</summary>
    private readonly record struct OverflowGeometry(int Overflow, string Tip, float X, float Y, float Width, float Height);

    /// <summary>
    /// 把设备分配到行（1 台单行；≥2 台两行均摊，上行 ⌈n/2⌉，下行 ⌊n/2⌋ 居中），
    /// 再算出每格的 X/Y/W/H。折叠格固定放在最后一行末尾。
    /// </summary>
    private static List<ChipGeometry> Layout(
        IReadOnlyList<DeviceStatus> devices, IReadOnlyList<string> overflowNames,
        int heightPx, float scale,
        Gdi.Font singleFont, Gdi.Font rowFont, Gdi.Font plainFont, Gdi.StringFormat sf,
        out float totalWidth, out OverflowGeometry? overflowChip)
    {
        var chips = new List<ChipGeometry>();
        overflowChip = null;
        totalWidth = 0;

        if (devices.Count == 0)
        {
            totalWidth = (float)(EmptyTextWidthDip * scale);
            return chips;
        }

        bool twoRows = devices.Count >= TwoRowThreshold;

        if (!twoRows)
        {
            // 单行：图标 + (闪电) + 百分比，与旧版一致
            float x = 0;
            foreach (var d in devices)
            {
                float w = SingleChipWidth(d, scale, singleFont, sf);
                chips.Add(new ChipGeometry(d, x, 0, w, heightPx));
                x += w;
            }
            totalWidth = x;
            return chips;
        }

        // 两行：上行 ⌈n/2⌉，下行 ⌊n/2⌋；少的那行居中
        int topCount = (devices.Count + 1) / 2;
        int bottomCount = devices.Count - topCount;

        float rowH = (heightPx - (float)(RowGapV * scale)) / 2f;
        float topY = 0;
        float bottomY = rowH + (float)(RowGapV * scale);

        var topDevices = devices.Take(topCount).ToList();
        var bottomDevices = devices.Skip(topCount).ToList();

        // 折叠格加到下行末尾
        float topW = RowWidth(topDevices, 0, scale, rowFont, plainFont, sf);
        float bottomW = RowWidth(bottomDevices, overflowNames.Count, scale, rowFont, plainFont, sf);
        totalWidth = Math.Max(topW, bottomW);

        PlaceRow(topDevices, 0, topY, rowH, totalWidth, scale, rowFont, sf, chips, ref overflowChip, overflowNames);
        PlaceRow(bottomDevices, overflowNames.Count, bottomY, rowH, totalWidth, scale, rowFont, sf, chips, ref overflowChip, overflowNames);

        return chips;
    }

    /// <summary>算一行总宽：格宽之和 + 格间距 + 可选折叠格宽度。</summary>
    private static float RowWidth(IReadOnlyList<DeviceStatus> row, int overflow, float scale,
        Gdi.Font rowFont, Gdi.Font plainFont, Gdi.StringFormat sf)
    {
        if (row.Count == 0 && overflow == 0) return 0;
        float w = 0;
        foreach (var d in row)
            w += RowChipWidth(d, scale, rowFont, sf);
        if (overflow > 0)
            w += Measure(MeasureG, $"+{overflow}", plainFont, sf);
        if (row.Count + (overflow > 0 ? 1 : 0) > 1)
            w += (float)(RowChipGap * scale) * (row.Count + (overflow > 0 ? 1 : 0) - 1);
        return w;
    }

    /// <summary>把一行设备（+ 可选折叠格）按"整行居中"摆到 chips；少的那行因此居中显示。</summary>
    private static void PlaceRow(
        IReadOnlyList<DeviceStatus> row, int overflow, float y, float rowH, float totalW, float scale,
        Gdi.Font rowFont, Gdi.StringFormat sf,
        List<ChipGeometry> chips, ref OverflowGeometry? overflowChip, IReadOnlyList<string> overflowNames)
    {
        int slots = row.Count + (overflow > 0 ? 1 : 0);
        if (slots == 0) return;

        // 行内容宽（不含居中留白）
        float contentW = 0;
        foreach (var d in row)
            contentW += RowChipWidth(d, scale, rowFont, sf);
        if (overflow > 0)
            contentW += Measure(MeasureG, $"+{overflow}", rowFont, sf);
        contentW += (float)(RowChipGap * scale) * (slots - 1);

        float x = (totalW - contentW) / 2f;   // 少的那行居中
        float gap = (float)(RowChipGap * scale);

        foreach (var d in row)
        {
            float w = RowChipWidth(d, scale, rowFont, sf);
            chips.Add(new ChipGeometry(d, x, y, w, rowH));
            x += w + gap;
        }
        if (overflow > 0)
        {
            float w = Measure(MeasureG, $"+{overflow}", rowFont, sf);
            string tip = string.Join("\n", overflowNames);
            overflowChip = new OverflowGeometry(overflow, tip, x, y, w, rowH);
        }
    }

    /// <summary>单行模式的一格宽：图标 + 百分比（闪电叠在图标中心，不占槽位）。</summary>
    private static float SingleChipWidth(DeviceStatus d, float scale, Gdi.Font font, Gdi.StringFormat sf)
    {
        float w = (float)(IconBox * scale) + (float)(IconTextGap * scale);
        return w + Measure(MeasureG, PercentText(d), font, sf);
    }

    /// <summary>两行模式的一格宽：图标 + 百分比（全部按 RowXxx 尺寸；闪电不占槽位）。</summary>
    private static float RowChipWidth(DeviceStatus d, float scale, Gdi.Font font, Gdi.StringFormat sf)
    {
        float w = (float)(RowIconBox * scale) + (float)(RowIconTextGap * scale);
        return w + Measure(MeasureG, PercentText(d), font, sf);
    }

    /// <summary>一格的前景色：自定义色优先；否则低电（≤20%）红、其余用主题前景（黑/白），与设备页保持一致。</summary>
    private static Gdi.Color AccentFor(DeviceStatus d, IReadOnlyDictionary<string, Gdi.Color>? accentColors, Skin skin)
    {
        if (accentColors != null && accentColors.TryGetValue(d.Id, out var custom)) return custom;
        return d.BatteryPercent is <= 20 ? UiPalette.ToDrawing(UiPalette.Low) : skin.Text;
    }

    /// <summary>按已算好的几何画一格：图标（按状态填充）+ 百分比。行内布局由 Layout 决定，这里只画。</summary>
    private static void DrawChipAt(
        Gdi.Graphics g, ChipGeometry chip, Skin skin, float padLeft, float scale,
        Gdi.Font font, Gdi.StringFormat sf, Gdi.Color state, int lowBatteryThreshold)
    {
        var d = chip.Device;
        var charging = DeviceStatusFormatter.IsCharging(d.Charging);

        // 单行用 IconBox；两行用 RowIconBox。判断依据是格子高度（行高必然小于总高）。
        bool compact = chip.Height < (float)(IconBox * scale) * 1.5f;
        double iconBoxDip = compact ? RowIconBox : IconBox;
        double iconTextGapDip = compact ? RowIconTextGap : IconTextGap;

        float iconBox = (float)(iconBoxDip * scale);
        float x = padLeft + chip.X;
        float iconY = chip.Y + (chip.Height - iconBox) / 2f;
        var iconRect = new Gdi.RectangleF(x, iconY, iconBox, iconBox);

        // 图标内部填充：充电绿 / 低电红 / 正常无填充
        Gdi.Color? fillColor = null;
        if (charging)
            fillColor = Gdi.Color.FromArgb(0xFF, 0x9E, 0xE0, 0x9E);   // 更浅绿
        else if (lowBatteryThreshold > 0 && d.BatteryPercent is int p && p <= lowBatteryThreshold)
            fillColor = Gdi.Color.FromArgb(0xFF, 0xF0, 0x9E, 0x9E);   // 更浅红

        DeviceIconFactory.DrawGlyph(g, d.Kind, skin.IconStroke, iconRect, scale, fillColor);

        x += iconBox + (float)(iconTextGapDip * scale);

        var text = PercentText(d);
        DrawText(g, text, font, state, new Gdi.RectangleF(x, chip.Y, Measure(g, text, font, sf) + 2, chip.Height), sf);
    }

    private static List<HitChip> BuildHits(
        List<ChipGeometry> chips, OverflowGeometry? overflowChip, float scale)
    {
        float padLeft = (float)(PadH * scale);
        var hits = new List<HitChip>();
        foreach (var c in chips)
        {
            hits.Add(new HitChip(
                c.Device.Id, c.Device.DisplayName,
                (int)Math.Floor(padLeft + c.X), (int)Math.Ceiling(c.Width)));
        }
        if (overflowChip is { } ov && !string.IsNullOrEmpty(ov.Tip))
        {
            hits.Add(new HitChip(
                OverflowHitId, ov.Tip,
                (int)Math.Floor(padLeft + ov.X), (int)Math.Ceiling(ov.Width)));
        }
        return hits;
    }

    private static void DrawText(Gdi.Graphics g, string text, Gdi.Font font, Gdi.Color color, Gdi.RectangleF rect, Gdi.StringFormat sf)
    {
        using var brush = new Gdi.SolidBrush(color);
        g.DrawString(text, font, brush, rect, sf);
    }

    private static float Measure(Gdi.Graphics g, string text, Gdi.Font font, Gdi.StringFormat sf) =>
        g.MeasureString(text, font, Gdi.PointF.Empty, sf).Width;

    private static string PercentText(DeviceStatus d) => d.BatteryPercent.HasValue ? $"{d.BatteryPercent}%" : "--";

    private static Gdi.Color Fade(Gdi.Color c, float factor) =>
        Gdi.Color.FromArgb((int)(c.A * factor), c.R, c.G, c.B);

    /// <summary>最右侧"有内容"的像素列（alpha &gt; 1，跳过铺底的 alpha=1）。裁掉右侧空白，宽度就不靠估算。</summary>
    private static int ContentRight(Gdi.Bitmap bmp)
    {
        var data = bmp.LockBits(
            new Gdi.Rectangle(0, 0, bmp.Width, bmp.Height), GdiImaging.ImageLockMode.ReadOnly,
            GdiImaging.PixelFormat.Format32bppPArgb);
        try
        {
            var row = new byte[bmp.Width * 4];
            int right = 0;
            for (int y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = bmp.Width - 1; x > right; x--)
                {
                    if (row[x * 4 + 3] > 1)
                    {
                        right = x;
                        break;
                    }
                }
            }
            return right;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
