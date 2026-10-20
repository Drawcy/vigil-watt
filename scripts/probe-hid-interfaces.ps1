#requires -Version 5.1
<#
只读探针：枚举当前所有 HID 接口的元数据（VID/PID/usage/报文长度），
用来确认某个 2.4G 接收器到底有没有暴露插件所需的 vendor collection。
不发任何字节、不打开长句柄（读元数据后立刻关闭），符合 docs/hardware/safety.md 零写入原则。

用法：
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-interfaces.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-interfaces.ps1 -Vid 1D57
#>
param(
    [string]$Vid = ""
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

public static class HidProbe
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

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_BUTTON_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public uint R1, R2, R3, R4, R5, R6, R7, R8, R9, R10;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct HIDP_VALUE_CAPS
    {
        public ushort UsagePage;
        public byte ReportID;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
        public ushort BitSize, ReportCount;
        public ushort R2A, R2B, R2C, R2D, R2E;
        public uint UnitsExp, Units;
        public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
        public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax;
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
    static extern int HidP_GetButtonCaps(int type, IntPtr buf, ref ushort len, IntPtr p);
    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetValueCaps(int type, IntPtr buf, ref ushort len, IntPtr p);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool HidD_GetManufacturerString(SafeFileHandle h, byte[] buf, uint len);

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

    static string Str(SafeFileHandle h, bool product)
    {
        var buf = new byte[512];
        bool ok = product ? HidD_GetProductString(h, buf, 512) : HidD_GetManufacturerString(h, buf, 512);
        return ok ? Encoding.Unicode.GetString(buf).TrimEnd('\0') : "";
    }

    static string ReportIds(IntPtr pre, int type)
    {
        var ids = new List<byte>();
        int btnSize = Marshal.SizeOf(typeof(HIDP_BUTTON_CAPS));
        int valSize = Marshal.SizeOf(typeof(HIDP_VALUE_CAPS));
        IntPtr b = Marshal.AllocHGlobal(btnSize * 64);
        IntPtr v = Marshal.AllocHGlobal(valSize * 64);
        try
        {
            ushort n = 64;
            if (HidP_GetButtonCaps(type, b, ref n, pre) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                {
                    var c = (HIDP_BUTTON_CAPS)Marshal.PtrToStructure(new IntPtr(b.ToInt64() + i * btnSize), typeof(HIDP_BUTTON_CAPS));
                    if (c.ReportID != 0 && !ids.Contains(c.ReportID)) ids.Add(c.ReportID);
                }
            n = 64;
            if (HidP_GetValueCaps(type, v, ref n, pre) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < n; i++)
                {
                    var c = (HIDP_VALUE_CAPS)Marshal.PtrToStructure(new IntPtr(v.ToInt64() + i * valSize), typeof(HIDP_VALUE_CAPS));
                    if (c.ReportID != 0 && !ids.Contains(c.ReportID)) ids.Add(c.ReportID);
                }
        }
        finally
        {
            Marshal.FreeHGlobal(b);
            Marshal.FreeHGlobal(v);
        }
        return ids.Count == 0 ? "0(无ID)" : string.Join(",", ids);
    }

    public static List<string> Scan(string vidFilter)
    {
        var lines = new List<string>();
        IntPtr set = SetupDiGetClassDevs(ref HidGuid, IntPtr.Zero, IntPtr.Zero, 2 | 16); // DIGCF_PRESENT | DIGCF_DEVICEINTERFACE
        if (set == IntPtr.Zero || set.ToInt64() == -1) { lines.Add("SetupDiGetClassDevs 失败"); return lines; }

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
                    Marshal.WriteInt32(mem, IntPtr.Size == 8 ? 8 : 6); // cbSize of detail struct
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref did, mem, req, out req, IntPtr.Zero)) continue;
                    var detail = (SP_DEVICE_INTERFACE_DETAIL_DATA)Marshal.PtrToStructure(mem, typeof(SP_DEVICE_INTERFACE_DETAIL_DATA));
                    string path = detail.DevicePath;

                    using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero)) // GENERIC_READ=0, share R/W, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL
                    {
                        if (h.IsInvalid) { lines.Add(string.Format("打不开(受保护) {0}", path)); continue; }
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr)) continue;

                        string vidPid = string.Format("{0:X4}:{1:X4}", attr.VendorID, attr.ProductID);
                        if (!string.IsNullOrEmpty(vidFilter) && !vidPid.StartsWith(vidFilter.ToUpperInvariant(), StringComparison.Ordinal)) continue;

                        IntPtr pre;
                        if (!HidD_GetPreparsedData(h, out pre)) continue;
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pre, ref caps) != HIDP_STATUS_SUCCESS) continue;
                            lines.Add(string.Format(
                                "VID:PID={0} usage=0x{1:X4}/0x{2:X4} in={3} out={4} feat={5} inID=[{6}] outID=[{7}] featID=[{8}] name='{9}' mfr='{10}'\r\n    {11}",
                                vidPid, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength,
                                caps.FeatureReportByteLength, ReportIds(pre, 0), ReportIds(pre, 1), ReportIds(pre, 2),
                                Str(h, true), Str(h, false), path));
                        }
                        finally { HidD_FreePreparsedData(pre); }
                    }
                }
                finally { Marshal.FreeHGlobal(mem); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return lines;
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp

$all = [HidProbe]::Scan($Vid)
Write-Output ("HID 接口共 {0} 个（只读，未向设备写入任何字节）" -f $all.Count)
Write-Output ""
$all | ForEach-Object { Write-Output $_ }
