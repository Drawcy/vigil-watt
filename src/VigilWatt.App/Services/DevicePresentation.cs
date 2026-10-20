using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 将用户自定义值应用到设备展示快照。
/// 2026/10/18 12:19:45 20211
/// </summary>
public static class DevicePresentation
{
    public static DeviceStatus Apply(DeviceStatus source, AppSettings settings)
        => DeviceCustomizationResolver.Apply(source, settings.DeviceCustomizations);

    /// <summary>
    /// 合并组专用：外观取组内**共用**的那一份（<see cref="MergedDevice.Customization"/>），
    /// 而不是代表通道自己的设置——否则 2.4G ↔ 蓝牙切换时名称/图标会跳回原值。
    /// </summary>
    public static DeviceStatus Apply(MergedDevice merged, AppSettings settings)
    {
        var result = merged.Primary.Clone();
        var custom = merged.Customization;
        if (custom is null) return result;
        if (!string.IsNullOrWhiteSpace(custom.Name)) result.Name = custom.Name;
        if (custom.IconKind is { } kind) result.Kind = kind;
        return result;
    }

    /// <summary>合并组的显示名（组级共用外观优先）。</summary>
    public static string DisplayName(MergedDevice merged, AppSettings settings)
        => Apply(merged, settings).DisplayName;

    public static bool HasIconOverride(string id, AppSettings settings) =>
        settings.DeviceCustomizations?.TryGetValue(id, out var custom) == true && custom.IconKind.HasValue;

    /// <summary>用户为该设备选的图标类型（含"自动"选择 Unknown）；未设置时回落到设备自身声明的类型。</summary>
    public static DeviceKind KindOf(string id, DeviceStatus source, AppSettings settings) =>
        settings.DeviceCustomizations?.TryGetValue(id, out var custom) == true && custom.IconKind is { } kind
            ? kind
            : source.Kind;

    /// <summary>合并组的图标类型：组级共用外观优先，否则用代表通道声明的类型。</summary>
    public static DeviceKind KindOf(MergedDevice merged, AppSettings settings)
        => merged.Customization?.IconKind ?? merged.Primary.Kind;

    /// <summary>用户为该设备选的自定义颜色；未设置/格式坏返回 null（调用方按默认规则着色）。</summary>
    public static Windows.UI.Color? AccentColor(string id, AppSettings settings)
    {
        if (settings.DeviceCustomizations is { } map
            && map.TryGetValue(id, out var custom)
            && custom is not null)
            return UiPalette.TryParseHex(custom.ColorHex);
        return null;
    }

    /// <summary>合并组的自定义颜色（组级共用外观）。</summary>
    public static Windows.UI.Color? AccentColor(MergedDevice merged)
        => UiPalette.TryParseHex(merged.Customization?.ColorHex);

    public static string DisplayName(string id, string fallback, AppSettings settings) =>
        DeviceCustomizationResolver.DisplayName(id, fallback, settings.DeviceCustomizations);
}
