using VigilWatt.Core.Models;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： DeviceMergeResolver / HistoryMergeResolver —— 同一设备多通道合并的纯逻辑。
/// 2026/10/20 12:20:00 xliu
/// 场景取自实机：一只鼠标的 2.4G（g3a:…，在线 46%）与蓝牙（ble:…#…，离线）。
/// </summary>
public class DeviceMergeResolverTests
{
    private const string Id24G = "g3a:ABC";
    private const string IdBle = "ble:BluetoothLE#BluetoothLE10:68-c0:11:73";

    private static DeviceStatus Make(string id, DeviceSource source, string name, int? percent,
                                     bool online, ChargingState charging = ChargingState.Discharging)
        => new()
        {
            Id = id,
            Name = name,
            Source = source,
            BatteryPercent = percent,
            Charging = charging,
            IsOnline = online,
        };

    private static readonly DeviceStatus Off24G =
        Make(Id24G, DeviceSource.Hid24G, "MCHOSE G3 A", 46, online: true);

    private static readonly DeviceStatus OffBle =
        Make(IdBle, DeviceSource.Ble, "MCHOSE G3 A", 44, online: false);

    [Fact]
    public void M1_未合并时两台设备各占一行()
    {
        var rows = DeviceMergeResolver.Build(new[] { Off24G, OffBle }, Array.Empty<IReadOnlyList<string>>());
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.IsMerged));
    }

    [Fact]
    public void M2_合并后只剩一行且任一通道在线即在线()
    {
        var rows = DeviceMergeResolver.Build(new[] { Off24G, OffBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } });

        var row = Assert.Single(rows);
        Assert.True(row.IsMerged);
        Assert.True(row.IsOnline);                       // 2.4G 在线 → 整组在线
        Assert.Equal(46, row.BatteryPercent);            // 取在线通道的值，不是离线的 44
        Assert.Equal(2, row.Channels.Count);
    }

    [Fact]
    public void M3_通道按2点4G在前蓝牙在后排序()
    {
        // 故意把蓝牙放前面，验证排序
        var rows = DeviceMergeResolver.Build(new[] { OffBle, Off24G },
            new IReadOnlyList<string>[] { new[] { IdBle, Id24G } });

        var row = Assert.Single(rows);
        Assert.Equal(DeviceSource.Hid24G, row.Channels[0].Source);
        Assert.Equal(DeviceSource.Ble, row.Channels[1].Source);
    }

    [Fact]
    public void M4_全部通道离线时整组离线且取最近更新的通道()
    {
        var old24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G3 A", 46, online: false);
        old24G.LastUpdated = DateTimeOffset.Now.AddHours(-2);
        var newBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G3 A", 44, online: false);
        newBle.LastUpdated = DateTimeOffset.Now;

        var rows = DeviceMergeResolver.Build(new[] { old24G, newBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } });

        var row = Assert.Single(rows);
        Assert.False(row.IsOnline);
        Assert.Equal(IdBle, row.Primary.Id);
        Assert.Equal(44, row.BatteryPercent);
    }

    [Fact]
    public void M5_组里只剩一个在位成员时退化为不合并()
    {
        // 蓝牙那条已被删除：只剩 2.4G
        var rows = DeviceMergeResolver.Build(new[] { Off24G },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } });

        var row = Assert.Single(rows);
        Assert.False(row.IsMerged);
        Assert.Single(row.Channels);
    }

    [Fact]
    public void M6_组内成员全不在时不留幽灵行()
    {
        var rows = DeviceMergeResolver.Build(Array.Empty<DeviceStatus>(),
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } });
        Assert.Empty(rows);
    }

    [Fact]
    public void M7_组合ID与成员顺序无关()
    {
        var a = DeviceMergeResolver.Build(new[] { Off24G, OffBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } })[0];
        var b = DeviceMergeResolver.Build(new[] { Off24G, OffBle },
            new IReadOnlyList<string>[] { new[] { IdBle, Id24G } })[0];
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void M8_Normalize去掉单元素组并保证ID只属一组()
    {
        var groups = DeviceMergeResolver.Normalize(new[]
        {
            new[] { Id24G, IdBle },
            new[] { Id24G, "other:1" },     // Id24G 已在第一组，这里只剩 other:1 → 整组作废
            new[] { "solo:a" },
        });

        var only = Assert.Single(groups);
        Assert.Equal(2, only.Count);
        Assert.Contains(Id24G, only);
        Assert.Contains(IdBle, only);
    }

    [Fact]
    public void M9_历史聚合按组合并条数且时间取并集()
    {
        var t0 = new DateTimeOffset(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);
        var stats = new List<DeviceHistoryStat>
        {
            new(IdBle, "MCHOSE G3 A", DeviceKind.Mouse, 5, t0.AddHours(3), t0.AddHours(4)),
            new(Id24G, "MCHOSE G3 A", DeviceKind.Mouse, 7, t0, t0.AddHours(1)),
        };

        var merged = HistoryMergeResolver.Merge(stats, new IReadOnlyList<string>[] { new[] { Id24G, IdBle } });

        var row = Assert.Single(merged);
        Assert.Equal(12, row.Count);
        Assert.Equal(t0, row.First);
        Assert.Equal(t0.AddHours(4), row.Last);
        Assert.Equal(HistoryMergeResolver.GroupId(new[] { Id24G, IdBle }), row.DeviceId);
    }

    [Fact]
    public void M10_历史曲线按组合ID展开成全部成员()
    {
        var groups = new IReadOnlyList<string>[] { new[] { Id24G, IdBle } };
        var groupId = HistoryMergeResolver.GroupId(new[] { Id24G, IdBle });

        // 展开顺序固定为字典序（"ble:" < "g3a:"），与组里怎么写无关
        Assert.Equal(new[] { IdBle, Id24G }, HistoryMergeResolver.ExpandIds(groupId, groups).ToArray());
        Assert.Equal(new[] { IdBle, Id24G },
            HistoryMergeResolver.ExpandIds(HistoryMergeResolver.GroupId(new[] { IdBle, Id24G }),
                new IReadOnlyList<string>[] { new[] { IdBle, Id24G } }).ToArray());
        // 不在组里的普通 ID 原样返回
        Assert.Equal(new[] { "solo:x" }, HistoryMergeResolver.ExpandIds("solo:x", groups));
    }

    [Fact]
    public void M13_蓝牙在线时2点4G通道被压制_取蓝牙值()
    {
        // 实机场景：键盘切到蓝牙，dongle 仍在位并应答缓存帧（2.4G 显示"在线 100%"），真实值来自蓝牙。
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: true);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: true);

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }));

        Assert.True(row.IsOnline);
        Assert.Equal(IdBle, row.Primary.Id);              // 代表值取蓝牙，不取那条假的 100%
        Assert.Equal(68, row.BatteryPercent);
        Assert.Single(row.Suppressed);                    // 2.4G 记入压制列表
        Assert.Equal(Id24G, row.Suppressed[0].Id);
        // 压制通道不进 Channels：徽标与指纹都只看采信的那条
        Assert.Single(row.Channels);
        Assert.Equal(DeviceSource.Ble, row.Channels[0].Source);
    }

    [Fact]
    public void M14_蓝牙在线且2点4G离线时也压制_不显示两条()
    {
        // 蓝牙连上后 dongle 那边通常会先被判离线；仍应只显示蓝牙一条
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: false);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: true);

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }));

        Assert.Equal(IdBle, row.Primary.Id);
        Assert.Single(row.Suppressed);
        Assert.Single(row.Channels);
    }

    [Fact]
    public void M15_蓝牙离线时2点4G正常显示不被压制()
    {
        // 反向：键盘在 2.4G 模式，蓝牙那条是历史残留（离线）→ 2.4G 必须照常显示
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: true);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: false);

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }));

        Assert.Equal(Id24G, row.Primary.Id);
        Assert.Equal(100, row.BatteryPercent);
        Assert.Empty(row.Suppressed);
        Assert.Equal(2, row.Channels.Count);
    }

    [Fact]
    public void M16_未合并时不做跨设备压制()
    {
        // 没有合并配置 = 用户没声明同体关系，两条各自独立显示（蓝牙不能压制任何 2.4G）
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: true);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: true);

        var rows = DeviceMergeResolver.Build(new[] { kb24G, kbBle }, Array.Empty<IReadOnlyList<string>>());

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Empty(r.Suppressed));
        Assert.Contains(rows, r => r.BatteryPercent == 100);
    }

    [Fact]
    public void M17_合并组外观整组共用_换通道不跳()
    {
        // 只在 2.4G 那条上定制过：切到蓝牙（Primary 变成 BLE）后外观必须仍然生效
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: false);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: true);
        var customs = new Dictionary<string, DeviceCustomization>
        {
            [Id24G] = new() { Name = "书房键盘", IconKind = DeviceKind.Headset, ColorHex = "#FF8800" },
        };

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }, null, customs));

        Assert.Equal(IdBle, row.Primary.Id);              // 代表通道是蓝牙
        Assert.Equal("书房键盘", row.Name);                // 名称仍来自组内共用的外观
        Assert.Equal(DeviceKind.Headset, row.Kind);
        Assert.Equal("#FF8800", row.Customization!.ColorHex);
    }

    [Fact]
    public void M18_合并组内都没定制时回落到代表通道自身()
    {
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: true);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: false);

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }, null,
            new Dictionary<string, DeviceCustomization>()));

        Assert.Null(row.Customization);
        Assert.Equal("MCHOSE G75 Pro", row.Name);
    }

    [Fact]
    public void M19_空的外观条目不算已定制()
    {
        // 旧版本可能留下只剩空值的条目；它不能把组内真正设置过的那份遮住
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G75 Pro", 100, online: false);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G75 Pro", 68, online: true);
        var customs = new Dictionary<string, DeviceCustomization>
        {
            // 字典序 BLE 在前，但条目是空的
            [IdBle] = new(),
            [Id24G] = new() { Name = "书房键盘" },
        };

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle },
            new IReadOnlyList<string>[] { new[] { Id24G, IdBle } }, null, customs));

        Assert.Equal("书房键盘", row.Name);
    }

    [Fact]
    public void M20_ApplyToGroup把同一份外观写给每个成员且互不共享实例()
    {
        var map = new Dictionary<string, DeviceCustomization>();
        var custom = new DeviceCustomization { Name = "书房键盘", ColorHex = "#112233" };

        int changed = MergedCustomization.ApplyToGroup(new[] { Id24G, IdBle }, map, custom);

        Assert.Equal(2, changed);
        Assert.Equal("书房键盘", map[Id24G].Name);
        Assert.Equal("书房键盘", map[IdBle].Name);
        Assert.NotSame(map[Id24G], map[IdBle]);           // 改其中一个不能连带改另一个

        // 恢复默认：组内两个键一起消失
        Assert.Equal(2, MergedCustomization.ApplyToGroup(new[] { Id24G, IdBle }, map, null));
        Assert.Empty(map);
    }

    [Fact]
    public void M21_合并后整组跟随目标设备的外观()
    {
        // 模拟合并：以 BLE（目标）的外观为准写给全组，主动方 2.4G 原来的名字被覆盖
        var map = new Dictionary<string, DeviceCustomization>
        {
            [Id24G] = new() { Name = "乱改的名字", ColorHex = "#000000" },
            [IdBle] = new() { Name = "键盘", IconKind = DeviceKind.Keyboard, ColorHex = "#FF0000" },
        };

        var source = MergedCustomization.Resolve(new[] { IdBle }, map);
        MergedCustomization.ApplyToGroup(new[] { Id24G, IdBle }, map, source);

        Assert.Equal("键盘", map[Id24G].Name);
        Assert.Equal("#FF0000", map[Id24G].ColorHex);
        Assert.Equal(DeviceKind.Keyboard, map[Id24G].IconKind);
        Assert.Equal("键盘", map[IdBle].Name);
    }

    [Fact]
    public void M22_组里残留已消失成员时组合ID仍按现存成员拼出()
    {
        // CR-71 真机场景：组里 3 个成员，其中 g3a:E74B… 已被删除（不在 devices 里）。
        // 行的 Id 只由现存成员拼成，与完整组拼出来的字符串不同——UI 侧必须能按交集回退。
        const string gone = "g3a:GONE";
        var kb24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G3 A", 46, online: false);
        var kbBle = Make(IdBle, DeviceSource.Ble, "MCHOSE G3 A", 44, online: false);
        var groups = new IReadOnlyList<string>[] { new[] { IdBle, Id24G, gone } };

        var row = Assert.Single(DeviceMergeResolver.Build(new[] { kb24G, kbBle }, groups));

        Assert.Equal(DeviceMergeResolver.GroupId(new[] { IdBle, Id24G }), row.Id);
        Assert.NotEqual(DeviceMergeResolver.GroupId(new[] { IdBle, Id24G, gone }), row.Id);
        Assert.Equal(2, row.MemberIds.Count);        // 幽灵成员不进 MemberIds
    }

    [Fact]
    public void M23_组里只剩幽灵成员时整行消失()
    {
        const string gone = "g3a:GONE";
        var groups = new IReadOnlyList<string>[] { new[] { IdBle, gone } };
        var rows = DeviceMergeResolver.Build(new[] { Off24G }, groups);
        var row = Assert.Single(rows);
        Assert.False(row.IsMerged);                  // 只剩 2.4G 独立一行
    }

    [Fact]
    public void M11_用户排序表决定行顺序_合并组按最靠前成员取位()
    {
        const string idKb = "hid:KB";
        var kb = Make(idKb, DeviceSource.Hid24G, "Keyboard", 80, online: false);
        var groups = new IReadOnlyList<string>[] { new[] { Id24G, IdBle } };

        // 排序表把 2.4G 放最后：整行（含蓝牙成员）跟着 2.4G 走
        var rows = DeviceMergeResolver.Build(new[] { Off24G, OffBle, kb }, groups,
                                             new[] { idKb, Id24G, IdBle });

        Assert.Equal(idKb, rows[0].Id);
        Assert.True(rows[1].IsMerged);
        Assert.Contains(Id24G, rows[1].MemberIds);
    }

    [Fact]
    public void M12_排序表里没有的设备按在线优先垫底()
    {
        const string idNew = "hid:NEW";
        var fresh = Make(idNew, DeviceSource.Hid24G, "AAA New", 50, online: true);
        var groups = new IReadOnlyList<string>[] { new[] { Id24G, IdBle } };

        var offline24G = Make(Id24G, DeviceSource.Hid24G, "MCHOSE G3 A", 46, online: false);
        var rows = DeviceMergeResolver.Build(new[] { offline24G, OffBle, fresh }, groups, Array.Empty<string>());

        Assert.Equal(idNew, rows[0].Id);       // 在线优先
        Assert.True(rows[1].IsMerged);         // 全离线的合并组在后
    }
}
