using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Plugins.G3a;

/// <summary>
/// 功 能： 迈从 G3 A 鼠标（VID_A8A5&amp;PID_2255）2.4G 插件 —— 纯被动，不向设备写任何字节。
/// 2026/10/20 12:00:00 xliu
/// 帧布局、全部实测样本与适配取舍见 docs/hardware/protocols.md §7。
///
/// 与其它被动插件的**本质区别**：设备只在「电源开关开机」那一下推一帧 `AA FA AE`，
/// 之后可以一整天不再说话（不复位也不推）。所以**不能**用沉默判离线——
/// 那会让一台正常工作的鼠标永远显示离线，比没有这个插件更糟。
/// </summary>
public sealed class G3aPlugin : BatteryPluginBase
{
    public const ushort MchoseVid = 0xA8A5;
    public const ushort G3aPid = 0x2255;

    public override string Name => "G3a";
    public override string DisplayName => "迈从";
    public override DeviceKind Kind => DeviceKind.Mouse;

    /// <summary>
    /// 纯被动零写入 → 允许 15 秒节拍重探。开机那一帧只存在一次，谁先读走就没了：
    /// 认领失败（冷却中/被别家认领）时越早重试越好。不发字节，重试没有写入代价。
    /// </summary>
    public override bool SupportsFastDiscovery => true;

    /// <summary>
    /// 鼠标开机后约 5 秒才推稳定帧（之前是 `[3]==0x0B` 的占位帧），窗口必须盖住它。
    /// 冷却必须 **明显小于 15 秒发现节拍**：开机帧只存在一次，探测窗口一旦错过就要等用户下次开关鼠标。
    /// 取 2 秒 = 每拍都能立刻重探，代价为零（探测不发任何字节）。
    /// </summary>
    protected override TimeSpan ProbeCooldown => TimeSpan.FromSeconds(2);

    /// <summary>本插件不存在任何写入路径；置 false 作为纵深防御。</summary>
    protected override bool WritesEnabled => false;

    /// <summary>探测只被动听、不发字节——不受写入开关约束。</summary>
    protected override bool ProbeWrites => false;

    /// <summary>认领后后台只读，开机帧到达时立即 StatusPushed。</summary>
    protected override bool SupportsPassiveListen => true;

    /// <summary>
    /// 离线判定：**全程不判离线**。
    ///
    /// 协议事实：设备只在开机时推一帧，此后静默（实测不操作 60 秒 + 操作鼠标 30 秒均为 0 帧）。
    /// 若按沉默判离线（其它被动插件都是这么做的），这台鼠标认领后很快就会被打成离线，
    /// 而且再也回不来——下一次推送要等用户重新开关鼠标。
    ///
    /// 所以这里三条全关：Silence = 0（不按沉默判）、Confirm = 0（不做延迟确认）、
    /// CountPollFailures = false（PollDevice 恒返回 null 是协议形态，不是失联）。
    /// 在线/离线改由**接口是否存在**决定：dongle 拔掉后 `AttachAsync` 的"已拔出设备"分支会把它移除；
    /// dongle 在位就当在线——这与系统托盘显示的"接收器插着"语义一致。
    /// </summary>
    protected override OfflinePolicy Offline { get; } = new()
    {
        Silence = TimeSpan.Zero,
        Confirm = TimeSpan.Zero,
        CountPollFailures = false,
    };

    /// <summary>
    /// 放宽到 60 点：这台鼠标**没有电量计**，读数由电压估算。插上充电线时电压被抬到满电电压，
    /// 读数会从 46 直接跳到 100（拔线后又回落），默认 20 点阈值会把这条真实状态整个拦掉，
    /// 表现为"插着电却一直显示放电、图标不填绿"。跳变都伴随充电标志翻转，见 docs/hardware/protocols.md §7.3。
    /// </summary>
    protected override int MaxPlausibleStepPercent => 60;

    /// <summary>
    /// 候选接口：`0xFF01/0x0010`、输入 64 字节（`mi_02`）。
    /// 该接口的 `out=65`，但本插件**从不写**，故不校验输出长度。
    /// </summary>
    protected override bool IsCandidate(HidDeviceInfo info)
        => info.VendorId == MchoseVid
           && info.ProductId == G3aPid
           && info.UsagePage == 0xFF01 && info.Usage == 0x0010
           && info.InputReportLength >= G3aCodec.MinLength;

    protected override (string Protocol, int? Percent, ChargingState Charging)? Probe(HidDeviceHandle handle)
        => ProbeAsync(handle).GetAwaiter().GetResult();

    /// <summary>
    /// 探测 = 被动听 14 秒。实测开机后约 5 秒才推稳定帧（14.4s 收到占位帧、19.1s 收到稳定帧），
    /// 窗口取 14 秒 ≈ 整个 15 秒发现节拍，保证开机那一帧不会被节拍相位漏掉。
    /// 探测**不发任何字节**，长窗口没有写入代价，只是让本插件的探测占住一个线程池线程。
    ///
    /// 只认 **稳定帧**：占位帧（`[3]==0x0B`）不返回读数——返回 null 让本轮探测失败，
    /// 由 <see cref="ProbeCooldown"/>（2 秒）与 15 秒发现节拍在下一次重试，
    /// 那时设备已稳定，能拿到真实电量。**不要把占位帧的 0 当成可得读数。**
    ///
    /// 听不到任何帧也是正常的（设备早就在了、今天没开关过）——返回 null，等用户开关鼠标。
    /// </summary>
    protected override async Task<(string Protocol, int? Percent, ChargingState Charging)?> ProbeAsync(
        HidDeviceHandle handle)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(14);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var slice = remaining < TimeSpan.FromMilliseconds(400) ? remaining : TimeSpan.FromMilliseconds(400);
            if (slice <= TimeSpan.Zero) break;

            var input = await handle.ReadInputAsync(slice).ConfigureAwait(false);
            if (input == null) continue;
            if (Log.IsEnabled(LogLevel.Debug)) Log.Debug($"迈从G3A 探测收到报文: {Log.Hex(input)}");

            var status = G3aCodec.Parse(input);
            if (status.Kind == G3aFrameKind.Valid)
                return ("g3a-aa-fa", status.Percent, status.Charging);

            if (status.Kind == G3aFrameKind.BootPlaceholder)
                Log.Once(LogLevel.Debug, $"{handle.Info.DevicePath}|g3a-boot",
                    $"迈从G3A 收到开机占位帧（电量恒 0，丢弃；等下一拍重探）: {Log.Hex(input)}");
        }
        return null;
    }

    /// <summary>读数全部来自被动帧；轮询不发帧也不读（避免与后台监听抢同一悬空读）。</summary>
    protected override (int? Percent, ChargingState Charging)? PollDevice(TrackedDevice _) => null;

    protected override (int? Percent, ChargingState Charging, bool Offline)? TryParsePassiveInput(
        TrackedDevice dev, byte[] input)
    {
        var status = G3aCodec.Parse(input);

        if (status.Kind == G3aFrameKind.BootPlaceholder)
        {
            // 开机瞬间 voltage 未稳，[9] 恒为 0。丢掉但**不记 Info**：每次开机都会来一条，不需要审计。
            if (Log.IsEnabled(LogLevel.Debug))
                Log.Debug($"迈从G3A 开机占位帧已丢弃（电量为 0）：{Log.Hex(input)}");
            return null;
        }

        if (status.Kind != G3aFrameKind.Valid) return null;

        Log.InfoOnChange($"g3a-status:{dev.Status.Id}", $"{status.Seq}|{status.Percent}|{status.Charging}",
            $"迈从G3A 状态帧: 电量={status.Percent}% {status.Charging} 序列号=0x{status.Seq:X2} 原始={Log.Hex(input)}");

        return (status.Percent, status.Charging, Offline: false);
    }

    protected override string BuildDisplayName(HidDeviceInfo info)
        => string.IsNullOrWhiteSpace(info.Product) ? $"迈从 G3 A ({info.VidPid})" : info.Product;
}
