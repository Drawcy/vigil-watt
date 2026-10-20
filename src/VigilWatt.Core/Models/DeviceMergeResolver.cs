namespace VigilWatt.Core.Models;

using VigilWatt.Core.Settings;

/// <summary>
/// 功 能： 同一个物理设备的多个通道（2.4G / 蓝牙）合并后的展示视图。
/// 2026/10/20 12:20:00 xliu
///
/// 背景：一只鼠标插着 2.4G 接收器、同时也能走蓝牙，两条通路的设备 ID 完全不同
/// （`g3a:...` 与 `ble:BluetoothLE#...`），名字也可能不一样，列表里会显示成两台。
/// 用户合并后，它们应当显示为**一行**。
///
/// 取值规则（见 docs/hardware/protocols.md 与 reviews/BACKLOG.md）：
/// - 电量/充电/名称/图标取**第一个在线通道**；全部离线时取最近的快照。
/// - 只要**任一通道在线**，整组就算在线。
/// - 徽标按通道逐个标注在线状态：`2.4G`（在线）/ `蓝牙`（离线灰）。
/// - **蓝牙在线时 2.4G 通道不采信**：dongle 仍枚举、仍应答缓存帧，但键盘已连蓝牙，
///   那条读数与"在线"都是假象（`Suppressed`，见 <see cref="DeviceMergeResolver.Build"/>）。
/// - 外观（名称/图标/颜色）整组共用一份（`Customization`），不随代表通道切换而变。
/// </summary>
public sealed class MergedDevice
{
    /// <summary>稳定 ID：组内成员 ID 排序后以 <c>+</c> 连接。成员变化会生成新 ID（用于排序/配色键）。</summary>
    public required string Id { get; init; }

    /// <summary>组内的原始设备 ID（已排序）。</summary>
    public required IReadOnlyList<string> MemberIds { get; init; }

    /// <summary>参与显示的各通道状态（按 2.4G → 蓝牙 → 其它 排序）。</summary>
    public required IReadOnlyList<DeviceStatus> Channels { get; init; }

    /// <summary>代表值：第一个在线通道；全离线时取最后更新的那个。</summary>
    public required DeviceStatus Primary { get; init; }

    /// <summary>组内任一通道在线即在线。</summary>
    public bool IsOnline => Channels.Any(c => c.IsOnline);

    /// <summary>被另一条在线蓝牙通道压制的 2.4G 通道（没有则为空列表）。</summary>
    public IReadOnlyList<DeviceStatus> Suppressed { get; init; } = Array.Empty<DeviceStatus>();

    /// <summary>组内共用的外观设置；未定制或单通道设备为 null（回落到设备自身值）。</summary>
    public DeviceCustomization? Customization { get; init; }

    public string Name => Customization is { Name: { Length: > 0 } custom } ? custom : Primary.DisplayName;

    public int? BatteryPercent => Primary.BatteryPercent;

    public ChargingState Charging => Primary.Charging;

    public DeviceKind Kind => Customization?.IconKind ?? Primary.Kind;

    /// <summary>是否为合并组（多于一个通道）。单通道时 UI 保持原样。</summary>
    public bool IsMerged => Channels.Count > 1;
}

/// <summary>把「原始设备集合 + 合并组配置」展开成展示用的设备列表（纯逻辑，可单测）。</summary>
public static class DeviceMergeResolver
{
    /// <summary>合并组的组合 ID：成员 ID 排序后以 <c>+</c> 连接。设备页、历史页共用同一个口径。</summary>
    public static string GroupId(IEnumerable<string> memberIds)
        => string.Join("+", memberIds.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>通道展示优先级：2.4G 在前，蓝牙在后（与徽标书写顺序一致）。</summary>
    private static int SourceRank(DeviceSource source) => source switch
    {
        DeviceSource.Hid24G => 0,
        DeviceSource.Ble => 1,
        DeviceSource.BluetoothClassic => 2,
        DeviceSource.System => 3,
        _ => 4,
    };

    /// <summary>
    /// 构建展示列表。
    /// </summary>
    /// <param name="devices">当前全部设备（含离线占位）。</param>
    /// <param name="groups">合并组：每项是若干设备 ID；单元素组会被忽略（等于没合并）。</param>
    /// <param name="order">
    /// 用户手动排序（原始设备 ID 列表，与 <c>AppSettings.DeviceOrder</c> 一致）。
    /// 空 = 只按「在线优先 + 名称」排。合并组取组内**最靠前成员**的位次，整行跟着走。
    /// </param>
    /// <param name="customizations">
    /// 外观设置（键为原始设备 ID）。合并组按它取一份**组级共用**的外观，
    /// 否则代表通道一变（2.4G ↔ 蓝牙），名称/图标就跳回原值。
    /// </param>
    public static List<MergedDevice> Build(IReadOnlyCollection<DeviceStatus> devices,
                                           IReadOnlyList<IReadOnlyList<string>> groups,
                                           IReadOnlyList<string>? order = null,
                                           IReadOnlyDictionary<string, DeviceCustomization>? customizations = null)
    {
        var byId = devices.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MergedDevice>(devices.Count);

        foreach (var group in groups)
        {
            if (group.Count < 2) continue;

            // 只保留当前真实存在的成员；组里全被删掉了就跳过（不留幽灵行）
            var members = group.Where(id => byId.ContainsKey(id) && used.Add(id))
                               .Select(id => byId[id])
                               .OrderBy(c => SourceRank(c.Source))
                               .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                               .ToList();
            if (members.Count == 0) continue;
            result.Add(BuildOne(members, customizations));
        }

        // 未参与合并的设备各自一行
        foreach (var d in devices)
        {
            if (used.Contains(d.Id)) continue;
            result.Add(BuildOne(new List<DeviceStatus> { d }, customizations));
        }

        return SortByUserOrder(result, order);
    }

    /// <summary>
    /// 按用户排序表稳定排序：整行取「组内最靠前成员的位次」，排序表里没有的垫底，
    /// 同组位次相同再按在线优先 → 名称，保证结果确定。
    /// </summary>
    private static List<MergedDevice> SortByUserOrder(List<MergedDevice> rows, IReadOnlyList<string>? order)
    {
        if (order is null || order.Count == 0)
            return rows.OrderByDescending(r => r.IsOnline)
                       .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                       .ToList();

        var rank = new Dictionary<string, int>(order.Count, StringComparer.Ordinal);
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i;

        return rows
            .OrderBy(r => r.MemberIds.Select(id => rank.TryGetValue(id, out var i) ? i : int.MaxValue)
                                     .DefaultIfEmpty(int.MaxValue).Min())
            .ThenByDescending(r => r.IsOnline)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MergedDevice BuildOne(List<DeviceStatus> channels,
                                         IReadOnlyDictionary<string, DeviceCustomization>? customizations)
    {
        // 蓝牙在线 ⇒ 设备连的是蓝牙，2.4G dongle 虽然还在位、也还会应答缓存帧，但那不是当前状态。
        // 只在**组内**压制：未合并的设备没有"另一条通道"可比，保持原样（用户手动合并即声明了二者同体）。
        bool bleOnline = channels.Any(c => c.Source == DeviceSource.Ble && c.IsOnline);
        var suppressed = bleOnline
            ? channels.Where(c => c.Source == DeviceSource.Hid24G).ToList()
            : new List<DeviceStatus>();

        var shown = channels.Where(c => !suppressed.Contains(c)).ToList();
        if (shown.Count == 0) shown = channels;   // 全被压制（组里只有 2.4G 在线）时兜底，绝不丢行

        var primary = shown.FirstOrDefault(c => c.IsOnline)
                      ?? shown.OrderByDescending(c => c.LastUpdated).First();

        // 外观整组共用：成员按字典序挑第一个真正设置过的（与代表通道无关），
        // 这样 2.4G ↔ 蓝牙切换时名称/图标不会跳。单通道设备等价于按自己取值。
        var memberIds = channels.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();

        return new MergedDevice
        {
            Id = GroupId(memberIds),
            MemberIds = memberIds,
            Channels = shown,
            Primary = primary,
            Suppressed = suppressed,
            Customization = MergedCustomization.Resolve(memberIds, customizations),
        };
    }

    /// <summary>把合并组配置（每项是成员 ID 列表）规范化：去重、去掉 &lt;2 的组、保证一个 ID 只属于一个组。</summary>
    public static List<List<string>> Normalize(IEnumerable<IEnumerable<string>> groups)
    {
        var result = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var members = new List<string>();
            foreach (var id in group)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!seen.Add(id)) continue;
                members.Add(id);
            }
            if (members.Count >= 2) result.Add(members);
            else foreach (var id in members) seen.Remove(id);   // 不足两个：把这个 ID 还回去，允许它进别的组
        }
        return result;
    }
}
