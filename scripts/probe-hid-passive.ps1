#requires -Version 5.1
<#
零写入被动监听探针：打开指定 HID 接口，只读 input report，打印原始帧并按时长退出。
用于确认"某台 2.4G 接收器会不会主动推电量帧"——协议未摸清前绝不发任何字节
（docs/hardware/safety.md、scripts/README.md「零写入原则」）。

用法：
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-passive.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\probe-hid-passive.ps1 -VidPid 1EA7:0064 -Interface col01 -Seconds 30
参数 -Interface 按接口路径里的 collection 名（col01/col02…）精确选择，空 = 自动选 vendor 通道 0xFFB5。
#>
param(
    [string]$VidPid = "1EA7:0064",
    [string]$Interface = "",
    [string]$PreferredUsage = "",
    [int]$Seconds = 30,
    [int]$MaxLines = 200
)

$ErrorActionPreference = 'Stop'

$sig = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class HidPassive
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
    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, uint len);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVICE_INTERFACE_DETAIL_DATA { public int cbSize; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)] public string DevicePath; }

    [StructLayout(LayoutKind.Sequential)]
    public struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

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
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, ref OVERLAPPED ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(SafeFileHandle h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetOverlappedResult(SafeFileHandle h, ref OVERLAPPED ov, out int read, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateEvent(IntPtr attr, bool manual, bool initial, IntPtr name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ResetEvent(IntPtr h);

    static Guid HidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
    const int HIDP_STATUS_SUCCESS = 0x00110000;
    const uint ERROR_IO_PENDING = 997;
    const uint WAIT_OBJECT_0 = 0;

    /// <summary>按 VID:PID 找全部 HID 接口，返回 "path\tusage\tin\tout\tname"。collection 为空 = 不限。</summary>
    public static List<string> Find(string vidPid, string collection)
    {
        var list = new List<string>();
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
                    if (!string.IsNullOrEmpty(collection)
                        && path.IndexOf(collection, StringComparison.OrdinalIgnoreCase) < 0) continue;

                    using (var h = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero))
                    {
                        if (h.IsInvalid) continue;
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf(typeof(HIDD_ATTRIBUTES)) };
                        if (!HidD_GetAttributes(h, ref attr)) continue;
                        string vp = string.Format("{0:X4}:{1:X4}", attr.VendorID, attr.ProductID);
                        if (!vp.Equals(vidPid, StringComparison.OrdinalIgnoreCase)) continue;

                        IntPtr pre;
                        if (!HidD_GetPreparedPreparsed(h, out pre)) continue;
                        try
                        {
                            var caps = new HIDP_CAPS();
                            if (HidP_GetCaps(pre, ref caps) != HIDP_STATUS_SUCCESS) continue;
                            if (caps.InputReportByteLength == 0) continue;
                            var buf = new byte[512];
                            HidD_GetProductString(h, buf, 512);
                            string name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                            list.Add(string.Format("{0}\t0x{1:X4}/0x{2:X4}\tin={3}\tout={4}\t{5}",
                                path, caps.UsagePage, caps.Usage, caps.InputReportByteLength, caps.OutputReportByteLength, name));
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

    static bool HidD_GetPreparedPreparsed(SafeFileHandle h, out IntPtr pre) { return HidD_GetPreparsedData(h, out pre); }

    /// <summary>只读监听：每个 input report 回一行 "HH:mm:ss.fff  +间隔ms  HEX"，直到时长到达或行数够多。</summary>
    public static List<string> Listen(string path, int inLen, int seconds, int maxLines)
    {
        var lines = new List<string>();
        // GENERIC_READ only + 共享读写：完全不请求写权限（=不发任何字节）
        using (var h = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero)) // FILE_FLAG_OVERLAPPED
        {
            if (h.IsInvalid)
                throw new InvalidOperationException("打开接口失败（可能被其他进程独占）Win32=" + Marshal.GetLastWin32Error());

            IntPtr ev = CreateEvent(IntPtr.Zero, true, false, IntPtr.Zero);
            try
            {
                var deadline = DateTime.Now.AddSeconds(seconds);
                var last = DateTime.MinValue;
                var deadlineLocal = deadline;
                var buf = new byte[inLen];
                while (DateTime.Now < deadlineLocal && lines.Count < maxLines)
                {
                    var ov = new OVERLAPPED();
                    ov.hEvent = ev;
                    ResetEvent(ev);
                    int read;
                    bool ok = ReadFile(h, buf, buf.Length, out read, ref ov);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != ERROR_IO_PENDING)
                        {
                            lines.Add("ReadFile 失败 Win32=" + err);
                            break;
                        }
                        int waitMs = (int)Math.Max(1, (deadlineLocal - DateTime.Now).TotalMilliseconds);
                        if (WaitForSingleObject(h, (uint)Math.Min(waitMs, 500)) != WAIT_OBJECT_0)
                        {
                            CancelIoEx(h, IntPtr.Zero);
                            continue;   // 超时：继续等，直到截止
                        }
                        if (!GetOverlappedResult(h, ref ov, out read, false)) continue;
                    }
                    if (read <= 0) continue;

                    var now = DateTime.Now;
                    string gap = last == DateTime.MinValue ? "     " : ((int)(now - last).TotalMilliseconds).ToString().PadLeft(5);
                    last = now;
                    var hex = new StringBuilder();
                    var n = Math.Min(read, buf.Length);
                    for (int i = 0; i < n; i++) hex.Append(buf[i].ToString("X2")).Append(' ');
                    lines.Add(string.Format("{0:HH:mm:ss.fff} +{1}ms  {2} ({3}B)",
                        now, gap, hex.ToString().TrimEnd(), n));
                    if (lines.Count >= 20)
                        Console.WriteLine("... 已收到 " + lines.Count + " 帧，继续监听至截止 ...");
                }
            }
            finally
            {
                try { CancelIoEx(h, IntPtr.Zero); } catch { }
                CloseHandle(ev);
            }
        }
        return lines;
    }
}
'@

Add-Type -TypeDefinition $sig -Language CSharp -ReferencedAssemblies System.Core

$targets = [HidPassive]::Find($VidPid, $Interface)
if ($targets.Count -eq 0) {
    Write-Output ("未找到 {0} 的输入接口（collection={1}）" -f $VidPid, $Interface)
    exit 1
}

Write-Output ("匹配到 {0} 个接口（GENERIC_READ 打开，零写入）：" -f $targets.Count)
foreach ($t in $targets) { Write-Output ("  " + $t) }
Write-Output ""

# 选路优先级：显式 PreferredUsage（配合 -Interface 双重过滤）> 0xFFB5 vendor 通道 > 第一个
$pick = $null
if ($PreferredUsage) {
    $pick = $targets | Where-Object { ($_ -split "`t")[1] -like $PreferredUsage } | Select-Object -First 1
}
if (-not $pick) {
    $pick = $targets | Where-Object { $_ -match '0xFFB5' } | Select-Object -First 1
}
if (-not $pick) { $pick = $targets | Select-Object -First 1 }
$parts = $pick -split "`t"
$listenPath = $parts[0]
$inLen = [int](($parts[2] -split '=')[1])

Write-Output ("监听: {0}" -f $parts[0])
Write-Output ("usage={0} in={1} 时长={2}s（只读，不发任何字节）" -f $parts[1], $inLen, $Seconds)
Write-Output "--------------------------------------------------------"

$lines = [HidPassive]::Listen($listenPath, $inLen, $Seconds, $MaxLines)

Write-Output "--------------------------------------------------------"
if ($lines.Count -eq 0) {
    Write-Output ("{0} 秒内未收到任何 input report（接口静默）" -f $Seconds)
} else {
    Write-Output ("共 {0} 条 input report：" -f $lines.Count)
    foreach ($l in $lines) { Write-Output ("  " + $l) }
}
