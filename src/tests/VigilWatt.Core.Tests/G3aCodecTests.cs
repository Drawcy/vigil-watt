using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins.G3a;
using Xunit;

namespace VigilWatt.Core.Tests;

/// <summary>
/// 功 能： G3aCodec —— 迈从 G3 A 鼠标状态帧（`AA FA A5` / `AA FA AE`）的纯解析。
/// 2026/10/20 12:00:00 xliu
/// 用例帧全部取自 USBPcap 真机抓包，见 docs/hardware/protocols.md §7.3.1。
/// </summary>
public class G3aCodecTests
{
    /// <summary>真机（powercycle3, 19.1s）：鼠标刚开机，46%。这是**唯一可被动获取**的帧头 `AE`。</summary>
    private static readonly byte[] PowerOnFrame = Convert.FromHexString(
        "aafaae3938010101d02e0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");

    /// <summary>真机（g3a2, 82.7s）：44%，与蓝牙读数一致。</summary>
    private static readonly byte[] Discharge44Frame = Frame(0xAE, 0x37, 44, 0x00);

    /// <summary>真机（powercycle3, 14.4s）：刚拨电源开关那一瞬，[3]=0x0B、[9]=0——占位帧。</summary>
    private static readonly byte[] BootPlaceholderFrame = Convert.FromHexString(
        "aafaae0b38010101d000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");

    /// <summary>真机（g3a4, 2.8s）：充电满电，帧头是网页用的 `A5`。</summary>
    private static readonly byte[] Charging100Frame = Convert.FromHexString(
        "aafaa57038010101d064010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");

    /// <summary>按实测布局造一帧 64 字节的完整报文。</summary>
    private static byte[] Frame(byte header2, byte seq, byte percent, byte chargingFlag)
    {
        var frame = new byte[64];
        frame[0] = 0xAA;
        frame[1] = 0xFA;
        frame[2] = header2;
        frame[3] = seq;
        frame[4] = 0x38;
        frame[5] = 0x01;
        frame[6] = 0x01;
        frame[7] = 0x01;
        frame[8] = 0xD0;
        frame[9] = percent;
        frame[10] = chargingFlag;
        return frame;
    }

    private static byte[] WithByte(byte[] frame, int index, byte value)
    {
        var copy = (byte[])frame.Clone();
        copy[index] = value;
        return copy;
    }

    [Fact]
    public void G1_开机帧解析出46并且是放电()
    {
        var s = G3aCodec.Parse(Frame(0xAE, 0x39, 46, 0x00));
        Assert.Equal(G3aFrameKind.Valid, s.Kind);
        Assert.Equal(46, s.Percent);
        Assert.Equal(ChargingState.Discharging, s.Charging);
        Assert.Equal(0x39, s.Seq);
    }

    [Fact]
    public void G2_充电满电帧解析出100与充电中()
    {
        var s = G3aCodec.Parse(Charging100Frame);
        Assert.Equal(G3aFrameKind.Valid, s.Kind);
        Assert.Equal(100, s.Percent);
        Assert.Equal(ChargingState.Charging, s.Charging);
    }

    [Fact]
    public void G3_开机占位帧必须被标成Placeholder而不是0电量()
    {
        var s = G3aCodec.Parse(BootPlaceholderFrame);
        Assert.Equal(G3aFrameKind.BootPlaceholder, s.Kind);
    }

    [Fact]
    public void G4_带reportID前缀的缓冲也能解析()
    {
        var prefixed = new byte[Discharge44Frame.Length + 1];
        prefixed[0] = 0x00;
        Array.Copy(Discharge44Frame, 0, prefixed, 1, Discharge44Frame.Length);

        var s = G3aCodec.Parse(prefixed);
        Assert.Equal(G3aFrameKind.Valid, s.Kind);
        Assert.Equal(44, s.Percent);
    }

    [Fact]
    public void G5_非AAFA帧一律忽略()
    {
        var other = Frame(0xAE, 0x37, 44, 0x00);
        other[1] = 0x55;   // 主机方向帧头
        Assert.Equal(G3aFrameKind.NotStatus, G3aCodec.Parse(other).Kind);
    }

    [Fact]
    public void G6_其他子帧头A5AE之外一律忽略()
    {
        Assert.Equal(G3aFrameKind.NotStatus, G3aCodec.Parse(Frame(0x37, 0x37, 44, 0x00)).Kind);
    }

    [Fact]
    public void G7_电量越界视为非本协议()
    {
        Assert.Equal(G3aFrameKind.NotStatus, G3aCodec.Parse(Frame(0xAE, 0x37, 101, 0x00)).Kind);
    }

    [Fact]
    public void G8_报文太短不越界()
    {
        var shortFrame = new byte[G3aCodec.MinLength - 1];
        shortFrame[0] = 0xAA;
        shortFrame[1] = 0xFA;
        shortFrame[2] = 0xAE;
        Assert.Equal(G3aFrameKind.NotStatus, G3aCodec.Parse(shortFrame).Kind);
    }

    [Fact]
    public void G9_充电标志非01时按放电处理()
    {
        var s = G3aCodec.Parse(Frame(0xAE, 0x37, 50, 0x02));
        Assert.Equal(G3aFrameKind.Valid, s.Kind);
        Assert.Equal(ChargingState.Discharging, s.Charging);
    }

    [Fact]
    public void G10_真机64字节帧整体可解析()
    {
        var s = G3aCodec.Parse(PowerOnFrame);
        Assert.Equal(G3aFrameKind.Valid, s.Kind);
        Assert.Equal(46, s.Percent);
        Assert.Equal(0x39, s.Seq);
    }
}
