using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 显示器目录：枚举当前所有显示器（设备名、是否主屏、矩形），供设置页"贴片显示在哪块屏"
/// 下拉框与 <see cref="TaskbarPillManager"/> 选屏使用。
/// 2026/10/18 16:40:00 xliu
/// 显示器设备名（\\.\DISPLAY1…）由系统分配，重排显示器后会变；本目录每次调用都重新枚举，
/// 由调用方决定怎么把"用户记忆"映射回"当前现实"。
/// </summary>
public static class MonitorCatalog
{
    /// <summary>一块显示器的描述。</summary>
    public readonly record struct MonitorEntry(
        IntPtr Handle,
        string DeviceName,
        bool IsPrimary,
        PixelRect Bounds);

    private static readonly Native.MonitorEnumProc EnumProc = EnumCallback;
    private static List<MonitorEntry>? _enumResults;

    /// <summary>枚举当前所有显示器。失败（譬如没权限）返回空列表，调用方按"只有主屏"兜底。</summary>
    public static IReadOnlyList<MonitorEntry> All()
    {
        var results = new List<MonitorEntry>();
        _enumResults = results;
        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, EnumProc, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn($"枚举显示器失败: {ex.Message}");
        }
        finally
        {
            _enumResults = null;
        }
        return results;
    }

    /// <summary>主屏条目；找不到（极端情况）返回 default。</summary>
    public static MonitorEntry Primary()
    {
        foreach (var m in All())
            if (m.IsPrimary) return m;
        return default;
    }

    /// <summary>按设备名找显示器；找不到返回 null。空字符串视为"主屏"。</summary>
    public static MonitorEntry? FindByDevice(string? deviceName)
    {
        var all = All();
        if (string.IsNullOrEmpty(deviceName))
        {
            foreach (var m in all)
                if (m.IsPrimary) return m;
            return all.Count > 0 ? all[0] : null;
        }
        foreach (var m in all)
            if (string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    /// <summary>任务栏句柄 → 它所在的显示器条目；找不到返回 null。</summary>
    public static MonitorEntry? MonitorOfTaskbar(IntPtr taskbarHwnd)
    {
        var mon = Native.MonitorFromWindow(taskbarHwnd, Native.MONITOR_DEFAULTTONEAREST);
        if (mon == IntPtr.Zero) return null;
        var info = new Native.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (!Native.GetMonitorInfoW(mon, ref info)) return null;
        bool isPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
        return new MonitorEntry(mon, info.szDevice, isPrimary,
            new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top,
                          info.rcMonitor.Right - info.rcMonitor.Left,
                          info.rcMonitor.Bottom - info.rcMonitor.Top));
    }

    private static bool EnumCallback(IntPtr hMonitor, IntPtr hdcMonitor, ref Native.RECT lprcMonitor, IntPtr dwData)
    {
        var info = new Native.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
        if (Native.GetMonitorInfoW(hMonitor, ref info))
        {
            bool isPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0;
            _enumResults?.Add(new MonitorEntry(hMonitor, info.szDevice, isPrimary,
                new PixelRect(info.rcMonitor.Left, info.rcMonitor.Top,
                              info.rcMonitor.Right - info.rcMonitor.Left,
                              info.rcMonitor.Bottom - info.rcMonitor.Top)));
        }
        return true;
    }
}
