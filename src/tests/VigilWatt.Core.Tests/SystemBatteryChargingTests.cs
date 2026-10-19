using VigilWatt.Core.Models;
using VigilWatt.Core.Power;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： 充电状态推导测试。
/// 2026/10/08 18:04:41 xliu
/// <c>GetSystemPowerStatus</c> 只回答"是否接着交流电"、不回答"是否正在充电"，而界面/历史库要的是 四态 <see cref="ChargingState"/>。
/// </summary>
public class SystemBatteryChargingTests
{
    private static SystemPowerSnapshot Snap(bool hasBattery, int? percent, bool acOnline)
        => new(hasBattery, percent, acOnline);

    [Fact]
    public void 接电且满电_判为已充满()
    {
        // 本机真机常态就是这一档（插着电、100%），必须显示"已充满"而不是"充电中"
        Assert.Equal(ChargingState.Full, SystemBatteryWatcher.DeriveCharging(Snap(true, 100, true)));
    }

    [Fact]
    public void 接电未满_判为充电中()
    {
        Assert.Equal(ChargingState.Charging, SystemBatteryWatcher.DeriveCharging(Snap(true, 63, true)));
    }

    [Fact]
    public void 未接电有电量_判为放电中()
    {
        Assert.Equal(ChargingState.Discharging, SystemBatteryWatcher.DeriveCharging(Snap(true, 71, false)));
    }

    [Fact]
    public void 未接电但电量未知_状态未知()
    {
        // 不能因为读不到百分比就猜"放电中"，那会在界面上凭空造出一个状态
        Assert.Equal(ChargingState.Unknown, SystemBatteryWatcher.DeriveCharging(Snap(true, null, false)));
    }

    [Fact]
    public void 接电但电量未知_状态未知()
    {
        Assert.Equal(ChargingState.Unknown, SystemBatteryWatcher.DeriveCharging(Snap(true, null, true)));
    }

    [Fact]
    public void 设备Id固定不变_保证历史曲线连续()
    {
        // 历史曲线、托盘记账、低电量"每档只提醒一次"都以这个 Id 为键，
        // 一旦它随会话变化，历史就会碎成一堆互不相连的曲线。
        Assert.Equal("sys:battery", SystemBatteryWatcher.DeviceId);
    }

    [Fact]
    public void 快照为Unavailable时_无电池()
    {
        var s = SystemPowerSnapshot.Unavailable;
        Assert.False(s.HasBattery);
    }

    // CR-66：设置页关→开本机电量监控。Stop 只清 _started，快照仍在；若 Start 不复位快照，
    // 重启后第一帧与旧快照"没有变化"，DeviceUpdated 永不触发，条目就这么消失了。
    [Fact]
    public void 停止后再启动_快照复位_首帧重新当作新设备推送()
    {
        using var watcher = new SystemBatteryWatcher();
        var pushes = new List<DeviceStatus>();
        watcher.DeviceUpdated += pushes.Add;

        watcher.Start();
        watcher.Refresh();

        // 这台机器没有电池时 Refresh 不会推送，本用例的前提不成立，直接跳过断言
        if (pushes.Count == 0) return;

        watcher.Stop();
        pushes.Clear();
        watcher.Start();
        watcher.Refresh();

        Assert.NotEmpty(pushes);
        Assert.Equal(SystemBatteryWatcher.DeviceId, pushes[^1].Id);
    }
}
