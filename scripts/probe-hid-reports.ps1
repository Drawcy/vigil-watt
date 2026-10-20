#requires -Version 5.1
<#
只读探针：导出某个 HID 接口的**报告描述符**，并把它解码成人类可读的 usage 树。
用来在完全不发字节的前提下，判断哪个 collection / report ID 承载电量字段
（docs/hardware/safety.md 零写入原则；读描述符不产生任何设备写入）。

用法：
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-reports.ps1 -VidPid A8A5:2255
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-reports.ps1 -VidPid A8A5:2255 -Interface mi_01
参数 -Interface 按接口路径片段过滤（col01 / mi_01 / mi_02 …），空 = 全部。
#>
param(
    [string]$VidPid = "A8A5:2255",
    [string]$Interface = "",
    [int]$MaxBytes = 1024
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class HidDesc
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
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint returned, IntPtr overlapped);

    const uint IOCTL_HID_GET_REPORT_DESCRIPTOR = 0x000B0000;

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

    /// <summary>枚举匹配 VID:PID 的接口，返回 "path\tusage信息"。</summary>
    static List<string> EnumPaths(string vidPid, string filter, out List<HIDP_CAPS> capsOut)
    {
        var list = new List<string>();
        capsOut = new List<HIDP_CAPS>();
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 2 | 16);
        if (set == IntPtr.Zero || set.ToInt64() == -1) return list;
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
                    if (!string.IsNullOrEmpty(filter) && path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

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
                            if (caps.InputReportByteLength == 0 && caps.OutputReportByteLength == 0) continue;
                            list.Add(path);
                            capsOut.Add(caps);
                        }
                        finally { HidD_FreePreparsedData(pre); }
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static string Hex(byte[] b, int n)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) { sb.Append(b[i].ToString("X2")); if ((i & 15) == 15) sb.AppendLine(); else sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }

    /// <summary>导出并解码报告描述符。返回可打印的多行字符串。</summary>
    public static List<string> Dump(string vidPid, string filter, int maxBytes)
    {
        var lines = new List<string>();
        List<HIDP_CAPS> capsList;
        var paths = EnumPaths(vidPid, filter, out capsList);
        if (paths.Count == 0) { lines.Add("未找到匹配接口"); return lines; }

        for (int k = 0; k < paths.Count; k++)
        {
            var caps = capsList[k];
            lines.Add("");
            lines.Add("================================================================");
            lines.Add(string.Format("usage=0x{0:X4}/0x{1:X4}  in={2} out={3} feat={4}", caps.UsagePage, caps.Usage,
                caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
            lines.Add(paths[k]);
            lines.Add("----------------------------------------------------------------");

            // READ|WRITE 打开：部分 dongle 的 vendor 接口不响应只有读权限的 IOCTL（返回 Win32=1）。
            // 仍然只调用 GET_REPORT_DESCRIPTOR，不写任何字节。
            using (var h = CreateFile(paths[k], 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
            {
                if (h.IsInvalid) { lines.Add("打开失败 Win32=" + Marshal.GetLastWin32Error()); continue; }
                var buf = new byte[maxBytes];
                uint got;
                // hid.dll 未导出 HidD_GetReportDescriptor（Win32=1），统一走 IOCTL。
                // 入参是 HIDD_DEVICE_STRING 头：cbSize(4) + Reserved(4) + Size(4，= 输出缓冲字节数)。
                // 头里 Size 填 0 时驱动直接返回 ERROR_INVALID_PARAMETER（Win32=1）。
                byte[] inBuf = new byte[12];
                BitConverter.GetBytes(maxBytes).CopyTo(inBuf, 8);
                if (!DeviceIoControl(h, IOCTL_HID_GET_REPORT_DESCRIPTOR, inBuf, 12, buf, (uint)maxBytes, out got, IntPtr.Zero))
                {
                    lines.Add("读描述符失败 Win32=" + Marshal.GetLastWin32Error());
                    continue;
                }
                lines.Add(string.Format("描述符 {0} 字节：", got));
                lines.Add(Hex(buf, (int)got));
                lines.Add("----------------------------------------------------------------");
                Decode(buf, (int)got, lines);
            }
        }
        return lines;
    }

    // ---------- 报告描述符解码（只解释结构，不做语义猜测） ----------

    sealed class Globals
    {
        public uint UsagePage = 0;
        public int LogMin = 0, LogMax = 0;
        public int ReportSize = 0, ReportCount = 0;
        public byte ReportId = 0;
    }

    static string UsageName(uint page, uint id)
    {
        switch (page)
        {
            case 0x0001:
                switch (id)
                {
                    case 0x02: return "Mouse";
                    case 0x05: return "GamePad";
                    case 0x06: return "Keyboard";
                    case 0x80: return "SystemControl";
                }
                break;
            case 0x000C:
                if (id == 0x01) return "ConsumerControl";
                break;
            case 0x0006:
                if (id == 0x02) return "BatteryStrength";
                break;
        }
        return "";
    }

    static readonly string[] Item = { "Main", "Global", "Local", "Reserved" };
    static readonly string[] MainTag = { "Input", "Output", "Collection", "Feature", "EndCollection" };
    static readonly string[] GlobalTag = { "UsagePage", "LogicalMin", "LogicalMax", "PhysicalMin", "PhysicalMax", "UnitExponent", "Unit", "ReportSize", "ReportId", "ReportCount", "Push", "Pop" };
    static readonly string[] LocalTag = { "Usage", "UsageMin", "UsageMax", "DesignatorIndex", "DesignatorMin", "DesignatorMax", "StringIndex", "StringMin", "StringMax", "Delimiter" };

    static void Decode(byte[] d, int n, List<string> lines)
    {
        var g = new Globals();
        var stack = new List<Globals>();
        var usages = new List<uint>();
        uint usageMin = 0xFFFFFFFF, usageMax = 0;
        int depth = 0;
        int bitIn = 0, bitOut = 0, bitFeat = 0;

        int i = 0;
        while (i < n)
        {
            byte prefix = d[i];
            if (prefix == 0xFE)          // 长条目
            {
                int longSize = d[i + 1];
                lines.Add(string.Format("  {0:X4}: 长条目（{1} 字节，跳过）", i, longSize));
                i += 3 + longSize;
                continue;
            }

            int sizeCode = prefix & 0x03;
            int size = sizeCode == 3 ? 4 : sizeCode;
            int type = (prefix >> 2) & 0x03;
            int tag = (prefix >> 4) & 0x0F;
            if (i + 1 + size > n) break;

            uint value = 0;
            for (int b = 0; b < size; b++) value |= (uint)d[i + 1 + b] << (8 * b);

            string tagName = "";
            if (type == 0 && tag < MainTag.Length) tagName = MainTag[tag];
            else if (type == 1 && tag < GlobalTag.Length) tagName = GlobalTag[tag];
            else if (type == 2 && tag < LocalTag.Length) tagName = LocalTag[tag];
            else tagName = Item[type] + " tag " + tag;

            string extra = "";
            if (type == 1)   // Global
            {
                switch (tag)
                {
                    case 0x0: g.UsagePage = value; break;
                    case 0x1: g.LogMin = (int)value; break;
                    case 0x2: g.LogMax = (int)value; break;
                    case 0x7: g.ReportSize = (int)value; break;
                    case 0x8: g.ReportId = (byte)value; break;
                    case 0x9: g.ReportCount = (int)value; break;
                    case 0xA: stack.Add(new Globals { UsagePage = g.UsagePage, LogMin = g.LogMin, LogMax = g.LogMax, ReportSize = g.ReportSize, ReportCount = g.ReportCount, ReportId = g.ReportId }); break;
                    case 0xB:
                        if (stack.Count > 0) { var p = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); g = p; }
                        break;
                }
                extra = "0x" + value.ToString("X");
            }
            else if (type == 2)  // Local
            {
                switch (tag)
                {
                    case 0x0: usages.Add(value); break;
                    case 0x1: usageMin = value; break;
                    case 0x2: usageMax = value; break;
                }
                extra = "0x" + value.ToString("X");
            }

            if (type == 0)   // Main
            {
                if (tag == 2) // Collection
                {
                    uint u = usages.Count > 0 ? usages[0] : 0;
                    depth++;
                    lines.Add(string.Format("  {0,4:X4}: {1} Collection  usage=0x{2:X4}", i, Pad(depth), u));
                }
                else if (tag == 4) // EndCollection
                {
                    depth = Math.Max(0, depth - 1);
                    lines.Add(string.Format("  {0,4:X4}: {1} EndCollection", i, Pad(depth)));
                }
                else if (tag == 0 || tag == 1 || tag == 3) // Input/Output/Feature
                {
                    int bits = g.ReportSize * g.ReportCount;
                    string kind = tag == 0 ? "Input" : (tag == 1 ? "Output" : "Feature");
                    int bitPos = tag == 0 ? bitIn : (tag == 1 ? bitOut : bitFeat);
                    lines.Add(string.Format(
                        "  {0,4:X4}: {1} {2,-8} id=0x{3:X2} bits@{4}={5}×{6} log[{7}..{8}] flags=0x{9:X} {10}",
                        i, Pad(depth), kind, g.ReportId, bitPos, g.ReportCount, g.ReportSize, g.LogMin, g.LogMax,
                        value, DescribeUsages(g.UsagePage, usages, usageMin, usageMax)));
                    if (tag == 0) bitIn += bits;
                    else if (tag == 1) bitOut += bits;
                    else bitFeat += bits;
                }
                usages.Clear(); usageMin = 0xFFFFFFFF; usageMax = 0;
            }

            i += 1 + size;
        }

        lines.Add("");
        lines.Add(string.Format("  [汇总] input 累计 {0} bit，output {1} bit，feature {2} bit",
            bitIn, bitOut, bitFeat));
    }

    /// <summary>把一段描述符解码成可打印行（供注册表路径调用）。</summary>
    public static List<string> DecodePublic(byte[] d)
    {
        var lines = new List<string>();
        Decode(d, d.Length, lines);
        return lines;
    }

    static string Pad(int depth) { return new string(' ', depth * 2) + "-"; }

    static string DescribeUsages(uint page, List<uint> usages, uint min, uint max)
    {
        var parts = new List<string>();
        foreach (var u in usages)
        {
            string nm = UsageName(page, u);
            parts.Add("0x" + u.ToString("X4") + (nm.Length > 0 ? "(" + nm + ")" : ""));
        }
        if (min != 0xFFFFFFFF) parts.Add(string.Format("0x{0:X4}..0x{1:X4}", min, max));
        return string.Join(",", parts.ToArray());
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp -ReferencedAssemblies System.Core

function Dump-DescriptorFromRegistry {
    param([string]$Vid, [string]$ProductId, [string]$Filter)

    $root = "HKLM:\SYSTEM\CurrentControlSet\Enum\HID"
    if (-not (Test-Path $root)) { Write-Output "注册表 HID 节点不存在"; return }

    $want = "VID_$($Vid.ToUpper())&PID_$($ProductId.ToUpper())"
    $hits = Get-ChildItem $root -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like "$want*" }

    if (-not $hits) { Write-Output ("注册表里没有 {0}" -f $want); return }

    foreach ($node in $hits) {
        foreach ($inst in (Get-ChildItem $node.PSPath -ErrorAction SilentlyContinue)) {
            if ($inst.PSChildName -eq "Device Parameters") { continue }
            $dp = Join-Path $inst.PSPath "Device Parameters"
            if (-not (Test-Path $dp)) { continue }
            $props = Get-ItemProperty $dp -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            if (-not ($props.PSObject.Properties.Name -contains "ReportDescriptor")) { continue }
            if ($Filter -and ($inst.PSPath -notmatch [regex]::Escape($Filter))) { continue }

            $desc = $props.ReportDescriptor
            if ($desc -isnot [byte[]] -or $desc.Length -eq 0) { continue }

            Write-Output ""
            Write-Output "================================================================"
            Write-Output ("注册表来源: {0}" -f $inst.PSPath)
            Write-Output ("描述符 {0} 字节" -f $desc.Length)
            Write-Output "----------------------------------------------------------------"
            $hex = for ($i = 0; $i -lt $desc.Length; $i++) { $desc[$i].ToString("X2") }
            Write-Output (($hex -join ' '))
            Write-Output "----------------------------------------------------------------"
            [HidDesc]::DecodePublic($desc) | ForEach-Object { Write-Output $_ }
        }
    }
}

$parts = $VidPid -split ':'
if ($parts.Count -eq 2) {
    Dump-DescriptorFromRegistry -Vid $parts[0] -ProductId $parts[1] -Filter $Interface
}
