namespace VigilWatt.Core.Models;

using VigilWatt.Core.Settings;

/// <summary>
/// 功 能： 合并组的外观规则——组内所有通道共用一套名称/图标/颜色，取值与写回都以整组为单位。
/// 2026/10/21 10:40:00 xliu
///
/// 为什么需要：外观按**单个设备 ID** 存，而合并组的代表通道会随在线状态变化（2.4G ↔ 蓝牙），
/// 若仍按"某个成员"取值，切换通道后名称/图标就跳回原值。这里按固定优先序挑一个模板，
/// 写回时把同一份值摊给组内每个成员，拆分后各通道也能各自保留。
/// </summary>
public static class MergedCustomization
{
    /// <summary>
    /// 取组内第一个"真正设置过外观"的成员 ID。无有效设置返回 null。
    /// 判据是**有字段非空**而不是"键存在"——只剩空条目的残留不能被当成已定制。
    /// </summary>
    public static string? SourceId(IReadOnlyList<string> memberIds,
                                   IReadOnlyDictionary<string, DeviceCustomization>? customizations)
    {
        if (customizations is null) return null;
        foreach (var id in memberIds)
        {
            if (customizations.TryGetValue(id, out var custom) && HasAnyValue(custom))
                return id;
        }
        return null;
    }

    /// <summary>组内共用的外观；没有任何设置过外观的成员时返回 null。</summary>
    public static DeviceCustomization? Resolve(IReadOnlyList<string> memberIds,
                                               IReadOnlyDictionary<string, DeviceCustomization>? customizations)
        => SourceId(memberIds, customizations) is { } id ? customizations![id] : null;

    public static bool HasAnyValue(DeviceCustomization? custom)
        => custom is not null
           && (!string.IsNullOrWhiteSpace(custom.Name) || custom.IconKind.HasValue ||
               !string.IsNullOrWhiteSpace(custom.ColorHex));

    /// <summary>
    /// 把一份外观写给组内**每个**成员；<paramref name="custom"/> 为空表示恢复默认（逐个移除）。
    /// 返回实际改动的条目数，供调用方决定是否落盘。
    /// </summary>
    public static int ApplyToGroup(IReadOnlyList<string> memberIds,
                                   IDictionary<string, DeviceCustomization> customizations,
                                   DeviceCustomization? custom)
    {
        int changed = 0;
        foreach (var id in memberIds)
        {
            if (custom is null)
            {
                if (customizations.Remove(id)) changed++;
                continue;
            }

            // 复制一份：各成员共享同一实例的话，之后改其中一个会连带改掉别的
            var copy = new DeviceCustomization
            {
                Name = custom.Name,
                IconKind = custom.IconKind,
                ColorHex = custom.ColorHex,
            };
            customizations[id] = copy;
            changed++;
        }
        return changed;
    }
}
