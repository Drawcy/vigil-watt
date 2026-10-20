using VigilWatt.Core.Models;

namespace VigilWatt.Core.Storage;

/// <summary>
/// 功 能： 把「按设备 ID 的历史聚合」按合并组折叠（纯逻辑，可单测）。
/// 2026/10/20 12:20:00 xliu
///
/// 同一台物理设备的 2.4G 与蓝牙通道在库里是两条独立记录（device_id 不同），
/// 用户合并后历史页/曲线都应看到一台。折叠规则：
/// - 组内各成员的**全部采样合并**成一条：条数相加，最早取最小、最新取最大；
/// - 名称取组内**最近有数据**的那个成员的名称；
/// - 组合 ID 与 <see cref="DeviceMergeResolver"/> 用同一套拼接规则（成员排序后以 + 连接），
///   这样设备页与历史页的排序/配色键一致。
/// </summary>
public static class HistoryMergeResolver
{
    /// <summary>合并组的组合 ID（与 <see cref="MergedDevice.Id"/> 一致）。</summary>
    public static string GroupId(IEnumerable<string> memberIds) => DeviceMergeResolver.GroupId(memberIds);

    /// <summary>
    /// 折叠聚合列表。<paramref name="stats"/> 是 <c>GetDeviceStats</c> 的原样输出（按最近更新倒序）。
    /// 不在任何组里的设备原样保留。
    /// </summary>
    public static List<DeviceHistoryStat> Merge(IReadOnlyList<DeviceHistoryStat> stats,
                                                IReadOnlyList<IReadOnlyList<string>> groups)
    {
        var byId = stats.ToDictionary(s => s.DeviceId, StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DeviceHistoryStat>(stats.Count);

        foreach (var group in groups)
        {
            var members = group.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            if (members.Count == 0) continue;
            foreach (var m in members) consumed.Add(m.DeviceId);

            if (members.Count == 1)
            {
                result.Add(members[0]);
                continue;
            }

            // stats 已按 Last 倒序，先到的是最近更新的那个 —— 名称、类型都听它的
            var newest = members.OrderByDescending(m => m.Last).First();
            result.Add(new DeviceHistoryStat(
                GroupId(members.Select(m => m.DeviceId)),
                newest.Name,
                newest.Kind,
                members.Sum(m => m.Count),
                members.Min(m => m.First),
                members.Max(m => m.Last)));
        }

        foreach (var s in stats)
            if (!consumed.Contains(s.DeviceId)) result.Add(s);

        return result.OrderByDescending(s => s.Last).ToList();
    }

    /// <summary>
    /// 把一组设备 ID 展开成「实际要从库里查的全部 device_id」。
    /// 曲线按组合 ID 查询时，需要把组内所有成员一起查出来再按时间归并。
    /// 返回顺序固定为字典序 —— 与 <see cref="GroupId"/> 的排序口径一致，调用方不必关心组的书写顺序。
    /// </summary>
    public static IReadOnlyList<string> ExpandIds(string deviceId, IReadOnlyList<IReadOnlyList<string>> groups)
    {
        foreach (var group in groups)
            if (GroupId(group) == deviceId)
                return group.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new[] { deviceId };
    }
}
