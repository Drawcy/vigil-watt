using VigilWatt.App.Services;
using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace VigilWatt.App.ViewModels;

/// <summary>合并组里的单个通道徽标（2.4G / 蓝牙）。在线用来源色，离线用灰色。</summary>
public sealed class ChannelBadge
{
    public string Text { get; init; } = "";
    public Brush TextBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);
    public Brush BackgroundBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>徽标**前置**分隔符：合并组里第一个徽标为空，其余为 " / "。</summary>
    public string Separator { get; init; } = "";
}

/// <summary>
/// 设备列表行（一次性绑定，每次刷新重建；Brush 在 UI 线程的刷新路径里创建）。
/// 2026/10/06 15:53:36 xliu
/// </summary>
public sealed class DeviceRowModel
{
    public string Id { get; init; } = "";
    public string IconGlyph { get; init; } = "\uE702";
    public string Name { get; init; } = "";
    public string PercentText { get; init; } = "--%";
    public double Progress { get; init; }
    public string SourceText { get; init; } = "";
    public string SubText { get; init; } = "";
    public bool IsCharging { get; init; }
    public bool IsOnline { get; init; }

    /// <summary>合并组内的通道徽标（单个通道時只有一项）。</summary>
    public IReadOnlyList<ChannelBadge> Badges { get; init; } = Array.Empty<ChannelBadge>();

    /// <summary>合并组内是否有多于一个通道（决定"拆分"菜单是否出现）。</summary>
    public bool IsMerged { get; init; }

    /// <summary>通道被另一条通道压制（如蓝牙在线时的 2.4G），UI 上灰显并注明原因。</summary>
    public bool HasSuppressed { get; init; }

    /// <summary>压制说明（副标题后缀）；没有压制时为空串。</summary>
    public string SuppressedLabel { get; init; } = "";

    /// <summary>离线设备才允许删除（在线设备显示的是排序按钮）。</summary>
    public bool IsOffline => !IsOnline;

    /// <summary>
    /// 离线设备显示删除按钮：**显式给 "Visible"**，不要直接绑 <see cref="IsOffline"/>。
    /// x:Bind 把 bool 绑到 Visibility 走隐式转换，转换失败时整条模板不生效——
    /// 表现就是"这一行的按钮和右键全都没反应"，而其它行正常。
    /// </summary>
    public Visibility OfflineVisibility => IsOffline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>在线设备显示上移/下移按钮（同上，显式给 Visibility）。</summary>
    public Visibility OnlineVisibility => IsOnline ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>来源徽标文字色（蓝牙蓝 / 2.4G 紫 / 本机青）。</summary>
    public Brush SourceBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>来源徽标胶囊底色（同色压淡）。</summary>
    public Brush SourceBackgroundBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>电量条着色：充电蓝 / 低电红 / 中等橙 / 正常绿 / 离线灰。</summary>
    public Brush ProgressBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>百分比文字：仅异常状态着色（自定义色 / 低电红 / 离线灰），正常态用主题前景色。
    /// 注意 x:Bind 绑 null 会压掉样式默认前景导致文字不可见，此属性必须非空。</summary>
    public Brush PercentBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    /// <summary>设备名称：与百分比/电量条同色（自定义色优先，否则主题前景）。</summary>
    public Brush NameBrush { get; init; } = UiPalette.Brush(UiPalette.Offline);

    public static DeviceRowModel From(DeviceStatus source, AppSettings settings)
        => From(new MergedDevice
        {
            Id = source.Id,
            MemberIds = new[] { source.Id },
            Channels = new[] { source },
            Primary = source,
        }, settings);

    /// <summary>
    /// 合并组外观的取值来源：组内**共用的那一份**设置（不随代表通道变化）。
    /// 返回 null 表示整组都没定制过，此时按代表通道自身声明的类型/颜色显示。
    /// </summary>
    public static DeviceCustomization? SharedCustomization(MergedDevice merged) => merged.Customization;

    /// <summary>合并组 → 一行。徽标逐通道标注在线状态：在线用来源色，离线统一灰色。</summary>
    public static DeviceRowModel From(MergedDevice merged, AppSettings settings)
    {
        var primary = merged.Primary;
        // 名称/图标/颜色同源：全部取组级共用外观，否则换通道后名称与图标会各用一套
        var d = DevicePresentation.Apply(merged, settings);
        var percent = merged.BatteryPercent;
        var custom = merged.Customization;
        var icon = GlyphFor(DevicePresentation.KindOf(merged, settings), d.DisplayName,
                            custom?.IconKind.HasValue == true);

        bool charging = DeviceStatusFormatter.IsCharging(merged.Charging);
        string state = DeviceStatusFormatter.ChargeSuffix(merged.Charging);
        if (!merged.IsOnline) state = " · 离线";
        var customColor = DevicePresentation.AccentColor(merged);

        // 与任务栏贴片同一口径：离线灰 / 自定义色 / 否则主题前景色（低电 ≤20% 才染红）
        Brush accentBrush = UiPalette.Brush(UiPalette.Accent(
            percent, charging, merged.IsOnline, customColor, UiPalette.FromResource("TextFillColorPrimaryBrush")));

        // 同一来源只占一个徽标：一个 dongle 可能被认领成两条 2.4G 接口（产品名不同、来源相同），
        // 逐条画会并排出现两个「2.4G」。在线状态取该来源里**任一在线**，避免同源一在线一离线时显示成灰。
        var badges = new List<ChannelBadge>();
        foreach (var group in merged.Channels.GroupBy(c => c.Source))
        {
            bool online = group.Any(c => c.IsOnline);
            // 在线用自己的来源色，离线统一灰（用户要求：2.4G 在线 + 蓝牙离线 → 2.4G 正常、蓝牙灰）
            var color = online ? UiPalette.SourceTag(group.Key) : UiPalette.Offline;
            badges.Add(new ChannelBadge
            {
                Text = DeviceSourceInfo.ShortLabel(group.Key),
                TextBrush = UiPalette.Brush(color),
                BackgroundBrush = UiPalette.Brush(UiPalette.Tint(color)),
                Separator = badges.Count > 0 ? " / " : "",
            });
        }

        // 被压制的通道（蓝牙在线时的 2.4G）追加灰徽标：不参与取值，但要让用户看见"它还在、只是不采信"。
        // 该来源已在徽标里出现过（同源多接口）就不再重复。
        foreach (var source in merged.Suppressed.Select(c => c.Source).Distinct())
        {
            if (badges.Any(b => b.Text == DeviceSourceInfo.ShortLabel(source))) continue;
            badges.Add(new ChannelBadge
            {
                Text = DeviceSourceInfo.ShortLabel(source),
                TextBrush = UiPalette.Brush(UiPalette.Offline),
                BackgroundBrush = UiPalette.Brush(UiPalette.Tint(UiPalette.Offline)),
                Separator = badges.Count > 0 ? " / " : "",
            });
        }

        string suppressedLabel = merged.Suppressed.Count == 0
            ? ""
            : " · " + string.Join("/", merged.Suppressed.Select(c => DeviceSourceInfo.ShortLabel(c.Source)).Distinct()) + " 已切走，不采信";

        return new DeviceRowModel
        {
            Id = merged.Id,
            IconGlyph = icon,
            Name = d.DisplayName,
            PercentText = DeviceStatusFormatter.PercentText(percent),
            Progress = percent ?? 0,
            SourceText = badges.Count > 0 ? badges[0].Text : DeviceSourceInfo.ShortLabel(primary.Source),
            Badges = badges,
            IsMerged = merged.IsMerged,
            HasSuppressed = merged.Suppressed.Count > 0,
            SuppressedLabel = suppressedLabel,
            SourceBrush = badges.Count > 0 ? badges[0].TextBrush : UiPalette.Brush(UiPalette.Offline),
            SourceBackgroundBrush = badges.Count > 0 ? badges[0].BackgroundBrush : UiPalette.Brush(UiPalette.Offline),
            SubText = $"{d.Protocol}{state}{suppressedLabel}",
            IsCharging = charging,
            IsOnline = merged.IsOnline,
            ProgressBrush = accentBrush,
            PercentBrush = accentBrush,
            NameBrush = accentBrush,
        };
    }

    /// <summary>
    /// 设备类型 → 图标字形（Segoe MDL2 Assets / Segoe Fluent Icons）。
    /// **优先用插件声明的 <see cref="DeviceStatus.Kind"/>**，没声明时才按显示名猜——
    /// 真机显示名里一个关键字都没有（"Rapoo Gaming Device"/"MCHOSE 2.4G Wireless"/"EDIFIER W820NB"），
    /// 靠名字猜必然退化成通用蓝牙图标。见 docs/project/handoff.md「第三轮：改显式声明设备类型」。
    /// </summary>
    private static string GlyphFor(DeviceKind kind, string displayName, bool customized)
    {
        if (kind == DeviceKind.Unknown && !customized) kind = DeviceKindGuesser.Guess(displayName);
        return kind switch
        {
            DeviceKind.Gamepad => "\uE7FC",   // Game
            DeviceKind.Mouse => "\uE962",     // Mouse
            DeviceKind.Keyboard => "\uE765",  // KeyboardClassic
            DeviceKind.Headset => "\uE7F6",   // Headphone
            DeviceKind.Speaker => "\uE7F5",   // Speakers
            DeviceKind.Computer => "\uE7F8",  // Devices2（显示器造型，代表本机）
            _ => "\uE702",                    // Bluetooth（兜底）
        };
    }
}
