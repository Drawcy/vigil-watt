using VigilWatt.Core.Models;

namespace VigilWatt.Core.Plugins.G3a;

/// <summary>`AA FA A5` / `AA FA AE` 状态帧的解析结果。</summary>
/// <param name="Kind">帧类别：不是本协议 / 有效电量 / 开机占位（必须丢弃）。</param>
/// <param name="Percent">电量百分比（仅 Valid 时有意义）。</param>
/// <param name="Charging">充电状态（仅 Valid 时有意义）。</param>
/// <param name="Seq">帧 [3] 字节。开机占位帧恒为 0x0B，稳定帧为 0x37~0x39 一类序列号。</param>
internal readonly record struct G3aStatus(G3aFrameKind Kind, int Percent, ChargingState Charging, byte Seq);

/// <summary>帧类别。</summary>
internal enum G3aFrameKind
{
    /// <summary>不是 `AA FA xx` 状态帧。</summary>
    NotStatus,
    /// <summary>有效电量。</summary>
    Valid,
    /// <summary>开机瞬间的占位帧（`[3]==0x0B`、电量为 0），必须丢弃，否则会误报 0% 低电量。</summary>
    BootPlaceholder,
}

/// <summary>
/// 功 能： 迈从 G3 A 鼠标（VID_A8A5&amp;PID_2255）的状态帧**纯解析**，不碰句柄、不写日志，便于单测。
/// 2026/10/20 12:00:00 xliu
/// 帧布局与全部实测样本见 docs/hardware/protocols.md §7.3.1。
/// </summary>
internal static class G3aCodec
{
    /// <summary>帧头第 1、2 字节固定值。</summary>
    public const byte Header1 = 0xFA;

    /// <summary>`AA FA A5`：官方网页驱动打开设备时推送。</summary>
    public const byte Header2Web = 0xA5;

    /// <summary>`AA FA AE`：鼠标电源开关开机时推送（**纯被动路径靠它**）。</summary>
    public const byte Header2PowerOn = 0xAE;

    /// <summary>开机占位帧的序列号字节，`[9]` 恒为 0。</summary>
    public const byte SeqBootPlaceholder = 0x0B;

    /// <summary>电量字节下标。</summary>
    public const int PercentIndex = 9;

    /// <summary>充电标志字节下标。</summary>
    public const int ChargingIndex = 10;

    /// <summary>序列号字节下标。</summary>
    public const int SeqIndex = 3;

    /// <summary>解析所需最短长度（下标 10 是最大访问位）。</summary>
    public const int MinLength = ChargingIndex + 1;

    /// <summary>充电标志：实测 `0x01` = 充电中，`0x00` = 放电。</summary>
    private const byte ChargingFlag = 0x01;

    /// <summary>
    /// 解析一条 input report。Windows 的 ReadInput 缓冲有时带 report ID 前缀（下标 0），
    /// 所以偏移 0/1 都试一次（与 <see cref="Mchose.MchoseV1Codec"/> 一致）。
    /// </summary>
    public static G3aStatus Parse(byte[] input)
    {
        foreach (int off in new[] { 0, 1 })
        {
            if (input.Length < off + MinLength) continue;
            if (input[off] != 0xAA || input[off + 1] != Header1) continue;

            byte sub = input[off + 2];
            if (sub != Header2Web && sub != Header2PowerOn) continue;

            byte seq = input[off + SeqIndex];
            int percent = input[off + PercentIndex];
            byte flag = input[off + ChargingIndex];

            // 开机占位帧：电压尚未稳定，[9] 恒为 0。绝不能当成"真的 0%"，否则会立刻误报低电量。
            if (seq == SeqBootPlaceholder) return new G3aStatus(G3aFrameKind.BootPlaceholder, 0, ChargingState.Unknown, seq);

            if (percent > 100) return new G3aStatus(G3aFrameKind.NotStatus, 0, ChargingState.Unknown, seq);

            return new G3aStatus(G3aFrameKind.Valid, percent,
                flag == ChargingFlag ? ChargingState.Charging : ChargingState.Discharging, seq);
        }
        return new G3aStatus(G3aFrameKind.NotStatus, 0, ChargingState.Unknown, 0);
    }
}
