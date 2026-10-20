#requires -Version 5.1
<#
只写一帧的验证探针：向迈从 G3A 的 mi_01 col04 (0xFF05) 发送官方抓包得到的电量查询帧，
然后只读等待应答，打印原始字节。

帧出处：docs/hardware/protocols.md（M HUB WEB + A8A5:2255 实测抓包，2026-10-20）
  发送：13 4A 01 00 … 末字节 = 前 19 字节和（0x5E）
  应答：13 4A 01 00 02 51 01 … 末字节 = 0xB2，[5]=电量，[6]=充电标志

只发这一帧，不做任何命令枚举（docs/hardware/safety.md §3 规则 1）。
用法（需先关闭 M HUB WEB，否则设备会被占住）：
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-mchose-g3a.ps1
#>
param(
    [string]$VidPid = "A8A5:2255",
    [int]$Seconds = 5
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class G3aProbe
{
    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr p);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr p);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr p, ref HIDP_CAPS c);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inLen,
                                       byte[] outBuf, uint outLen, out uint returned, IntPtr ov);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, uint len);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string DevicePath; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid g, uint index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    static Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(b[i].ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }

    /// <summary>找 0xFF05 那条接口（in=64, out=0），返回 path。找不到返回空串。</summary>
    static string FindInterface(string vidPid, out int inLen, out int featLen)
    {
        return FindInterface(vidPid, out inLen, out featLen, 0xFF05, 0x0088);
    }

    /// <summary>按 usage 找接口。</summary>
    static string FindInterface(string vidPid, out int inLen, out int featLen, int wantPage, int wantUsage)
    {
        inLen = 0;
        featLen = 0;
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 2 | 16);
        if (set == IntPtr.Zero || set.ToInt64() == -1) return "";
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref did); i++)
            {
                int req;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr mem = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, mem, req, out req, IntPtr.Zero)) continue;
                    var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA)Marshal.PtrToStructure(mem, typeof(SP_DEVICE_INTERFACE_DETAIL_DATA));
                    string path = detail.DevicePath;

                    using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        string vp = string.Format("{0:X4}:{1:X4}", attr.VendorID, attr.ProductID);
                        if (!vp.Equals(vidPid, StringComparison.OrdinalIgnoreCase)) continue;

                        IntPtr pre;
                        if (!HidD_GetPreparsedData(h, out pre)) continue;
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pre, ref caps) != HIDP_STATUS_SUCCESS) continue;
                            if (caps.UsagePage == wantPage && caps.Usage == wantUsage)
                            {
                                inLen = caps.InputReportByteLength;
                                featLen = caps.FeatureReportByteLength;
                                return path;
                            }
                        }
                        finally { HidD_FreePreparsedData(pre); }
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return "";
    }

    /// <summary>列出该 VID/PID 在给定接口号下的全部 HID 路径（path + 报告长度）。</summary>
    public static List<string> ListPaths(string vidPid)
    {
        var outp = new List<string>();
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 2 | 16);
        if (set == IntPtr.Zero || set.ToInt64() == -1) return outp;
        try
        {
            var did = new SP_DEVICE_INTERFACE_DATA();
            did.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref HidGuid, i, ref did); i++)
            {
                int req;
                SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out req, IntPtr.Zero);
                IntPtr mem = Marshal.AllocHGlobal(req);
                try
                {
                    Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, mem, req, out req, IntPtr.Zero)) continue;
                    var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA)Marshal.PtrToStructure(mem, typeof(SP_DEVICE_INTERFACE_DETAIL_DATA));
                    string path = detail.DevicePath;

                    using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        string vp = string.Format("{0:X4}:{1:X4}", attr.VendorID, attr.ProductID);
                        if (!vp.Equals(vidPid, StringComparison.OrdinalIgnoreCase)) continue;

                        IntPtr pre;
                        if (!HidD_GetPreparsedData(h, out pre)) continue;
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pre, ref caps) != HIDP_STATUS_SUCCESS) continue;
                            outp.Add(string.Format("{0}\t0x{1:X4}/0x{2:X4}\tin={3}\tout={4}\tfeat={5}",
                                path, caps.UsagePage, caps.Usage, caps.InputReportByteLength,
                                caps.OutputReportByteLength, caps.FeatureReportByteLength));
                        }
                        finally { HidD_FreePreparsedData(pre); }
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return outp;
    }

    /// <summary>发一帧 0x13/0x4A 电量查询，再只读收集应答。返回可打印行。</summary>
    public static List<string> Run(string vidPid, int seconds)
    {
        var lines = new List<string>();

        // 官方抓包（A8A5:2255, M HUB WEB, 2026-10-20）：
        //   bmRequestType=0x21 / bRequest=SET_REPORT / wValue=0x0213(Output,id=0x13) / wIndex=1(接口1) / 20 字节
        //   应答在 ep 0x82：13 4A 01 00 02 51 01 … 校验 0xB2，[5]=81% 电量，[6]=充电标志
        // 接口 1 = mi_01。它在 Windows 里被拆成 4 个 collection，0x13 属于"整条接口"，
        // 而 hidclass 按 collection 校验，故只能逐个 collection 试 HidD_SetOutputReport。
        foreach (var p in ListPaths(vidPid))
        {
            var parts = p.Split('\t');
            string path = parts[0];
            if (path.IndexOf("&mi_01&", StringComparison.OrdinalIgnoreCase) < 0
                && path.IndexOf("&mi_02#", StringComparison.OrdinalIgnoreCase) < 0) continue;
            lines.Add("--- 尝试 " + path);
            lines.Add("    caps: " + string.Join(" ", parts, 1, parts.Length - 1));
            lines.AddRange(TrySend(path, seconds, lines.Count));
        }
        if (lines.Count == 0) lines.Add("未找到 mi_01 的任何接口");
        return lines;
    }

    /// <summary>在单个接口路径上发一帧查询并等应答。</summary>
    static List<string> TrySend(string path, int seconds, int baseCount)
    {
        var lines = new List<string>();
        var frame = new byte[20];
        frame[0] = 0x13;
        frame[1] = 0x4A;
        frame[2] = 0x01;
        byte sum = 0;
        for (int i = 0; i < 19; i++) sum += frame[i];
        frame[19] = sum;

        using (var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            if (h.IsInvalid) { lines.Add("    打开失败 Win32=" + Marshal.GetLastWin32Error()); return lines; }

            // HidD_SetOutputReport 要求缓冲区长度 == 该接口的 OutputReportByteLength：
            //   mi_02 是 65（Win32=87 说明它认识 report 0x13，只是长度不对），其余 collection 的 out=0。
            // 逐长度试一次是安全的：都是同一条官方帧，只是补齐到接口要求的整帧长度。
            foreach (int len in new[] { 20, 65, 64 })
            {
                var buf = new byte[len];
                Array.Copy(frame, 0, buf, 0, Math.Min(frame.Length, len));
                bool ok = HidD_SetOutputReport(h, buf, (uint)len);
                lines.Add(string.Format("    HidD_SetOutputReport({0}B) => {1} Win32={2}", len, ok, Marshal.GetLastWin32Error()));
                if (!ok) continue;

                lines.Add("    发送成功，监听 " + seconds + " 秒…");
                var deadline = DateTime.Now.AddSeconds(seconds);
                var rd = new byte[Math.Max(len, 65)];
                var task = System.Threading.Tasks.Task.Run(() =>
                {
                    int n;
                    return ReadFile(h, rd, rd.Length, out n, IntPtr.Zero) ? n : 0;
                });
                if (!task.Wait((int)Math.Max(1, (deadline - DateTime.Now).TotalMilliseconds)))
                {
                    lines.Add("    （超时未收到应答）");
                    return lines;
                }
                int got = task.Result;
                lines.Add(got > 0
                    ? string.Format("    收到 {0}B: {1}", got, Hex(rd, Math.Min(got, 40)))
                    : "    读取返回 0 字节");
                return lines;
            }
            return lines;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr ov);
}
'@

Add-Type -TypeDefinition $sig -Language CSharp -ReferencedAssemblies System.Core

$lines = [G3aProbe]::Run($VidPid, $Seconds)
foreach ($l in $lines) { Write-Output $l }
