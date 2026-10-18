using System.Text;
using Microsoft.UI.Dispatching;
using VigilWatt.Core.Utils;

namespace VigilWatt.App.Services;

/// <summary>
/// 功 能： 任务栏电量贴片管理器：按设置在**一块**屏幕上显示贴片（默认主屏，可在设置页改）。
/// 2026/10/18 12:00:00 xliu
/// 老版本是"主屏 + 每块副屏各贴一块"，与 TrafficMonitor 等任务栏工具共存时会视觉重叠；
/// 改成单选屏后，用户可把贴片放到空闲屏。选中屏的任务栏消失（拔插显示器）时贴片隐藏不删，
/// 屏回来后自动复贴——避免拖动记忆被意外清掉。
/// </summary>
public sealed class TaskbarPillManager : IDisposable
{
    private const int ReconcileIntervalMs = 2000;

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _timer;
    private TaskbarPillWindow? _pill;

    /// <summary>当前贴片挂在哪块屏；空 = 还没贴上（屏不在 / 没任务栏）。</summary>
    private string? _attachedDevice;
    private bool _disposed;

    public TaskbarPillManager()
    {
        Reconcile();
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(ReconcileIntervalMs);
        _timer.Tick += (_, _) => Reconcile();
        _timer.Start();
    }

    /// <summary>设置保存后由设置页调用：可能换了屏，立即重对齐。</summary>
    public void RefreshFromSettings()
    {
        _pill?.RefreshFromSettings();
        Reconcile();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        try { _pill?.Dispose(); } catch (Exception ex) { Log.Warn($"任务栏贴片释放失败: {ex.Message}"); }
        _pill = null;
        _attachedDevice = null;
    }

    /// <summary>对齐"设置里选中的屏"与"当前贴片位置"。</summary>
    private void Reconcile()
    {
        if (_disposed) return;
        try
        {
            var target = MonitorCatalog.FindByDevice(App.Services.Settings.TaskbarBatteryMonitorDevice);
            if (target == null)
            {
                Detach();   // 显示器被拔了：拆掉贴片，等回来再贴
                return;
            }

            // 那块屏上得有任务栏才贴；Win11 副屏可以关任务栏
            var taskbar = FindTaskbarOnMonitor(target.Value.Handle);
            if (taskbar == IntPtr.Zero)
            {
                Detach();
                return;
            }

            if (_pill != null && _attachedDevice == target.Value.DeviceName)
                return;   // 已贴在正确的屏上，啥也不用做

            Detach();
            _pill = new TaskbarPillWindow(taskbar);
            _attachedDevice = target.Value.DeviceName;
        }
        catch (Exception ex)
        {
            Log.Warn($"任务栏贴片管理器协调失败: {ex.Message}");
        }
    }

    private void Detach()
    {
        if (_pill == null) return;
        try { _pill.Dispose(); } catch (Exception ex) { Log.Warn($"任务栏贴片释放失败: {ex.Message}"); }
        _pill = null;
        _attachedDevice = null;
    }

    /// <summary>指定显示器上的任务栏句柄（主屏 Shell_TrayWnd；副屏 Shell_SecondaryTrayWnd）。找不到返回零。</summary>
    private static IntPtr FindTaskbarOnMonitor(IntPtr hMonitor)
    {
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            if (Native.GetClassNameW(hwnd, sb, sb.Capacity) <= 0) return true;
            var name = sb.ToString();
            if (name != Native.TaskbarClassName && name != Native.SecondaryTaskbarClassName) return true;
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST) == hMonitor)
            {
                found = hwnd;
                return false;   // 找到就停
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
