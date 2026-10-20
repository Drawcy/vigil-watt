using System.Collections.Concurrent;
using VigilWatt.Core.Ble;
using VigilWatt.Core.Hid;
using VigilWatt.Core.Models;
using VigilWatt.Core.Plugins;
using VigilWatt.Core.Power;
using VigilWatt.Core.Settings;
using VigilWatt.Core.Storage;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Devices;

/// <summary>
/// 功 能： 统一设备管理（中枢）：合并 BLE + 经典蓝牙 + 2.4G 插件状态，写历史、发变更事件；插件反射自动发现，接口认领经 DeviceClaimRegistry 防抢。
/// 2026/10/06 15:53:36 xliu
/// 详见 docs/project/architecture.md §1。
/// </summary>
public sealed class DeviceManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly HistoryStore _history;
    private readonly BleBatteryWatcher _ble = new();
    private readonly BluetoothClassicWatcher _classic = new();
    private readonly SystemBatteryWatcher _system = new();
    private readonly List<IBatteryPlugin> _plugins;
    private readonly DeviceClaimRegistry _claims = new();
    private readonly ConcurrentDictionary<string, DeviceStatus> _devices = new();

    /// <summary>每台设备"是否已就低电量提醒过"。电量回到阈值之上（或充电）就复位，下次跌破再提醒一次；
    /// 设备条目被移除时也一并清掉（见 <see cref="ExpireTombstones"/> / <see cref="DropDevicesOf"/>，CR-23）。</summary>
    private readonly ConcurrentDictionary<string, bool> _lowBatteryNotified = new();

    /// <summary>每台设备上次写进历史的采样时间（UTC）。定时采样据此去重：离线条目保留旧读数，不按 LastUpdated 重复写假平台（CR-67）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSampledAt = new();
    /// <summary>由历史记录补出的离线占位条目（见 <see cref="RestoreOfflineTombstones"/>）；过期剔除时只能删这些，不能误删"在位但读不到"的真实设备。</summary>
    private readonly HashSet<string> _tombstoneIds = new();
    private System.Threading.Timer? _pollTimer;
    private System.Threading.Timer? _sampleTimer;
    private System.Threading.Timer? _discoveryTimer;
    private System.Threading.Timer? _cleanupTimer;
    private System.Threading.Timer? _systemBatteryTimer;
    private readonly object _pollSync = new();
    /// <summary>所有 pass（整轮轮询 / 快速发现）串行执行——插件内部的字典不是线程安全的。</summary>
    private readonly SemaphoreSlim _passGate = new(1, 1);
    private Task _currentPoll = Task.CompletedTask;
    private Task _currentDiscovery = Task.CompletedTask;

    /// <summary>单台设备状态有更新（含低电量提醒触发）。</summary>
    public event Action<DeviceStatus>? DeviceUpdated;

    /// <summary>设备列表可能增删（全量刷新 UI 用）。</summary>
    public event Action? DevicesChanged;

    /// <summary>低于阈值触发提醒（App 层弹 Toast/托盘变色）。</summary>
    public event Action<DeviceStatus>? LowBatteryDetected;

    /// <summary>已发现的插件（设置页动态生成开关用）。</summary>
    public IReadOnlyList<IBatteryPlugin> Plugins => _plugins;

    public DeviceManager(AppSettings settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        _plugins = DiscoverPlugins();
        _ble.DeviceUpdated += d => Upsert(d);
        _classic.DeviceUpdated += Upsert;
        _system.DeviceUpdated += Upsert;
        // 罗技等插件的 HID++ 自发通知（插拔电）不走 60 秒轮询，直接推送
        foreach (var p in _plugins)
            p.StatusPushed += Upsert;
    }

    /// <summary>反射发现 Core 程序集里的全部插件。新增品牌 = 加一个 IBatteryPlugin 实现类文件，无需改这里。</summary>
    private static List<IBatteryPlugin> DiscoverPlugins()
    {
        var list = new List<IBatteryPlugin>();
        foreach (var type in typeof(IBatteryPlugin).Assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(IBatteryPlugin).IsAssignableFrom(type)) continue;
            var ctor = type.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Log.Warn($"插件 {type.Name} 缺少无参构造，已跳过");
                continue;
            }
            try
            {
                list.Add((IBatteryPlugin)ctor.Invoke(null));
            }
            catch (Exception ex)
            {
                Log.Error($"插件 {type.Name} 实例化失败", ex);
            }
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Log.Info($"插件发现完成: {string.Join(", ", list.Select(p => p.Name))}");
        return list;
    }

    /// <summary>设备列表：按用户手动排序（<see cref="AppSettings.DeviceOrder"/>）；未排序的新设备垫底，按显示名字典序稳定。</summary>
    public IReadOnlyCollection<DeviceStatus> Devices
    {
        get
        {
            var order = _settings.DeviceOrder;
            var rank = new Dictionary<string, int>(order.Count);
            for (int i = 0; i < order.Count; i++) rank[order[i]] = i;
            return _devices.Values
                .OrderByDescending(d => d.IsOnline)
                .ThenBy(d => rank.TryGetValue(d.Id, out var r) ? r : int.MaxValue)
                .ThenBy(d => d.DisplayName)
                .ToList();
        }
    }

    /// <summary>
    /// 把设备在 <see cref="AppSettings.DeviceOrder"/> 里上移/下移一行并落盘；调用方负责触发 UI 刷新。
    ///
    /// 排序表存的是**原始设备 ID**（含合并组的每个成员），但界面传进来的是**行 ID**——
    /// 合并后一行对应组合 ID（<c>a+b</c>），直接 IndexOf 永远找不到，表现为"点了上移没反应"。
    /// 故先把行 ID 展开成成员，再以「相邻行」为单位交换；整行与相邻行可能是不同长度的合并组，
    /// 所以用块交换而不是逐 ID 平移，否则短的那行会被长的那行挤散。
    /// </summary>
    public void MoveInOrder(string rowId, int delta)
    {
        if (delta == 0) return;
        var order = _settings.DeviceOrder;
        var groups = MergeGroups;

        var members = HistoryMergeResolver.ExpandIds(rowId, groups)
            .Where(id => _devices.ContainsKey(id))
            .OrderBy(id => order.IndexOf(id) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (members.Count == 0) return;

        // 行没进过排序表（新设备）：按当前显示顺序补到末尾，否则 IndexOf 找不到、表现为点了没反应
        if (!members.Any(order.Contains))
            foreach (var id in members) if (!order.Contains(id)) order.Add(id);

        // order 里也可能残留已不存在的 ID，逐行遍历时跳过
        var rows = new List<List<string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            if (!_devices.ContainsKey(id) || !seen.Add(id)) continue;
            var group = groups.FirstOrDefault(g => g.Contains(id, StringComparer.Ordinal));
            if (group is null)
            {
                rows.Add(new List<string> { id });
                continue;
            }
            var row = new List<string>();
            foreach (var x in group)
                if (_devices.ContainsKey(x) && seen.Add(x)) row.Add(x);
            rows.Add(row);
        }

        int index = rows.FindIndex(r => r.Contains(members[0], StringComparer.Ordinal));
        if (index < 0) return;
        int other = index + delta;
        if (other < 0 || other >= rows.Count) return;

        var rebuilt = new List<string>(order.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == index) rebuilt.AddRange(rows[other]);
            else if (i == other) rebuilt.AddRange(rows[index]);
            else rebuilt.AddRange(rows[i]);
        }

        // 排序表里原本就有但当前不在 _devices 里的 ID，原样保留在末尾
        rebuilt.AddRange(order.Where(id => !seen.Contains(id)));
        order.Clear();
        order.AddRange(rebuilt);
        _settings.Save();
    }

    /// <summary>
    /// 删除一台**已离线**的设备：从列表移除，并清掉自定义名称/图标/颜色与排序；
    /// 同时记入 <see cref="AppSettings.HiddenDeviceIds"/>，阻止重启后从历史记录复活。
    /// 设备再次上线时按新设备处理（详见 <see cref="Upsert"/>）。
    /// </summary>
    public bool RemoveOfflineDevice(string id)
    {
        if (!_devices.TryGetValue(id, out var device) || device.IsOnline) return false;

        _devices.TryRemove(id, out _);
        _tombstoneIds.Remove(id);
        _lowBatteryNotified.TryRemove(id, out _);

        _settings.DeviceOrder.Remove(id);
        _settings.DeviceCustomizations.Remove(id);
        if (!_settings.HiddenDeviceIds.Contains(id)) _settings.HiddenDeviceIds.Add(id);

        // 合并组里也要摘掉：不摘的话组里永远留着这条已不在列表的 ID，行的组合 ID
        // 与现存成员拼不回来 → 该行之后无法再删除/改名（CR-71 真机复现）
        var groups = DeviceMergeResolver.Normalize(_settings.DeviceMergeGroups);
        bool groupChanged = false;
        var cleaned = new List<List<string>>(groups.Count);
        foreach (var group in groups)
        {
            if (!group.Remove(id)) { cleaned.Add(group); continue; }
            groupChanged = true;
            if (group.Count >= 2) cleaned.Add(group);   // 只剩一个：整组作废，该设备恢复为独立行
        }
        if (groupChanged) _settings.DeviceMergeGroups = cleaned;

        _settings.Save();

        Log.Info($"已删除离线设备: {id}");
        DevicesChanged?.Invoke();
        return true;
    }

    public DeviceStatus? Find(string id) => _devices.TryGetValue(id, out var d) ? d : null;

    /// <summary>
    /// 合并组（已规范化，每组 ≥2 个 ID，一个 ID 只属于一组）。UI 与历史聚合都读它。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> MergeGroups
        => DeviceMergeResolver.Normalize(_settings.DeviceMergeGroups);

    /// <summary>
    /// 把若干设备合并成一台。会先**从原有组里摘掉**这些 ID（避免一个 ID 同时在两组里），
    /// 再连同它们的旧组一起合并成新组——例如 2.4G 已和 A 合并，再把蓝牙合进来，结果是三者一组。
    /// </summary>
    public bool MergeDevices(IReadOnlyCollection<string> ids)
    {
        var incoming = ids.Where(id => _devices.ContainsKey(id))
                          .Distinct(StringComparer.Ordinal)
                          .ToList();
        if (incoming.Count < 2) return false;

        var merged = new List<string>(incoming);
        var remaining = new List<List<string>>();
        foreach (var group in DeviceMergeResolver.Normalize(_settings.DeviceMergeGroups))
        {
            var hit = group.Where(incoming.Contains).ToList();
            if (hit.Count == 0)
            {
                remaining.Add(group);
                continue;
            }
            // 同组里没被点名的成员一并带过来
            foreach (var id in group)
                if (!merged.Contains(id, StringComparer.Ordinal)) merged.Add(id);
        }

        remaining.Add(merged);
        _settings.DeviceMergeGroups = remaining;
        _settings.Save();
        Log.Info($"已合并设备: {string.Join(" + ", merged)}");
        RefreshPresentation();
        return true;
    }

    /// <summary>拆开一个合并组。行仍按各通道分别显示。</summary>
    public bool SplitDevice(IReadOnlyCollection<string> ids)
    {
        var target = new HashSet<string>(ids, StringComparer.Ordinal);
        var groups = DeviceMergeResolver.Normalize(_settings.DeviceMergeGroups);
        if (!groups.Any(g => g.Any(target.Contains))) return false;

        _settings.DeviceMergeGroups = groups.Where(g => !g.Any(target.Contains)).ToList();
        _settings.Save();
        Log.Info($"已拆分设备: {string.Join(", ", target)}");
        RefreshPresentation();
        return true;
    }

    /// <summary>该设备 ID 所在的合并组（不在任何组里返回 null）。</summary>
    public IReadOnlyList<string>? GroupOf(string id)
        => MergeGroups.FirstOrDefault(g => g.Contains(id, StringComparer.Ordinal));

    public void RefreshPresentation()
    {
        foreach (var device in Devices) DeviceUpdated?.Invoke(device);
        DevicesChanged?.Invoke();
    }

    public void Start()
    {
        _ble.Start();
        _classic.Start();
        if (_settings.MonitorSystemBattery) _system.Start();
        RestoreOfflineTombstones();
        StartTimers();
        Log.Info("DeviceManager 已启动");
    }

    /// <summary>离线占位（墓碑）保留时长。存在的理由（蓝牙设备消失时从没进过列表、总数恒等无信息量）见 docs/project/code-flow.md §5.1。</summary>
    private static readonly TimeSpan TombstoneWindow = TimeSpan.FromHours(24);

    /// <summary>把"最近 <see cref="TombstoneWindow"/> 内有历史、当前不在位"的设备补成离线条目；只在启动后跑一次。</summary>
    private void RestoreOfflineTombstones()
    {
        try
        {
            var cutoff = DateTimeOffset.Now - TombstoneWindow;
            int added = 0;
            foreach (var stat in _history.GetDeviceStats())
            {
                if (stat.Last < cutoff) continue;
                if (_devices.ContainsKey(stat.DeviceId)) continue;

                // 用户手动删除过的设备：不要靠历史记录把它复活（回来后按新设备处理）
                if (_settings.HiddenDeviceIds.Contains(stat.DeviceId)) continue;

                // 用户主动关掉的品牌插件，不要靠历史记录把它"复活"成离线条目——
                // 那会让"关了开关却还显示"的老问题换个形式回来。
                var owner = OwnerOf(stat.DeviceId);
                if (owner.Length > 0 && !IsPluginEnabledByName(owner, defaultOn: true)) continue;

                // 本机电量同理：开关关着时，历史里的系统电量记录不该把它复活成离线占位。
                if (stat.DeviceId == SystemBatteryWatcher.DeviceId && !_settings.MonitorSystemBattery) continue;

                // 2.4G 插件的设备 ID 带插件名前缀；离线占位优先复用插件声明的类型，不从品牌产品名猜。
                var pluginKind = _plugins
                    .FirstOrDefault(p => string.Equals(p.Name, owner, StringComparison.OrdinalIgnoreCase))?.Kind
                    ?? DeviceKind.Unknown;

                Upsert(new DeviceStatus
                {
                    Id = stat.DeviceId,
                    Name = stat.Name,
                    Source = SourceFromId(stat.DeviceId),
                    Kind = stat.Kind != DeviceKind.Unknown ? stat.Kind
                        : pluginKind != DeviceKind.Unknown ? pluginKind
                        : DeviceKindGuesser.Guess(stat.Name),
                    Protocol = "历史记录",
                    BatteryPercent = null,   // 不拿历史值冒充当前电量
                    Charging = ChargingState.Unknown,
                    LastUpdated = stat.Last,
                    IsOnline = false,
                });
                _tombstoneIds.Add(stat.DeviceId);
                added++;
            }
            if (added > 0)
                Log.Info($"已补 {added} 台离线设备占位（最近 {TombstoneWindow.TotalHours:0} 小时内有历史、当前不在位）");
        }
        catch (Exception ex)
        {
            Log.Error("恢复离线设备占位失败", ex);
        }
    }

    private static DeviceSource SourceFromId(string id) =>
        id.StartsWith("btc:", StringComparison.Ordinal) ? DeviceSource.BluetoothClassic
        : id.StartsWith("ble:", StringComparison.Ordinal) ? DeviceSource.Ble
        : id.StartsWith("sys:", StringComparison.Ordinal) ? DeviceSource.System
        : DeviceSource.Hid24G;

    /// <summary>剔除超期的离线占位。真实设备（哪怕离线）不在此列——它们由插件继续维护。</summary>
    private void ExpireTombstones()
    {
        if (_tombstoneIds.Count == 0) return;
        var cutoff = DateTimeOffset.Now - TombstoneWindow;
        var expired = new List<string>();
        foreach (var id in _tombstoneIds)
        {
            if (_devices.TryGetValue(id, out var d) && (d.IsOnline || d.LastUpdated > cutoff)) continue;
            expired.Add(id);
        }
        if (expired.Count == 0) return;

        foreach (var id in expired)
        {
            _devices.TryRemove(id, out _);
            _tombstoneIds.Remove(id);
            _lowBatteryNotified.TryRemove(id, out _);
        }
        Log.Info($"离线占位已过期移除 {expired.Count} 台");
        DevicesChanged?.Invoke();
    }

    private void StartTimers()
    {
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        _systemBatteryTimer?.Dispose();
        // 首扫尽快跑（探测已是并行/短路，不再需要 2 秒缓冲）
        _pollTimer = new Timer(_ => _ = RequestPollAsync(), null,
            TimeSpan.FromMilliseconds(300), TimeSpan.FromMinutes(1));
        // 快速发现节拍：见 RequestDiscoveryAsync（设备一醒/开机就十几秒内进列表，而非苦等 60 秒主轮询）
        _discoveryTimer = new Timer(_ => _ = RequestDiscoveryAsync(), null,
            FastDiscoveryInterval, FastDiscoveryInterval);
        // 首拍与主轮询错开 20 秒：同一拍里轮询的 Upsert 和这里会给同一台设备写两条同 ts 的点（CR-67 实测）。
        _sampleTimer = new Timer(_ => SafeSample(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(Math.Max(1, _settings.SampleIntervalMinutes)));
        // 本机电源独立高频采样：插电/拔电属于"用户立刻就想看到"的状态，跟 60s 主轮询走会明显迟钝。
        // 读的是 GetSystemPowerStatus（纯内存读），没有外设耗电问题，可以放心拉到 5s。
        if (_settings.MonitorSystemBattery)
            _systemBatteryTimer = new Timer(_ => SafeSystemBatteryRefresh(), null,
                TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
        // 历史保留定时执行：以前只在手动保存设置时清理，UI 文案写"自动清理"却是空话（CR-04）。
        // 启动后 30 秒首跑（避开启动高峰），之后每 6 小时一次。
        _cleanupTimer = new Timer(_ => CleanupHistoryNow(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
    }

    /// <summary>快速发现节拍。15 秒 = 雷柏心跳(3.04s)的约 5 倍（发现时延上界 ~15s）；迈从 ProbeCooldown 必须 &lt; 15s 才能每拍重探。</summary>
    private static readonly TimeSpan FastDiscoveryInterval = TimeSpan.FromSeconds(15);

    /// <summary>设置变化后调用（阈值/采样间隔/插件开关）。</summary>
    public void ApplySettings()
    {
        ApplySystemBatterySetting();
        StartTimers();
        // 立刻跑一轮：否则"关掉插件"要等最多 60 秒的下一个轮询才撤掉设备，
        // 期间列表/托盘仍显示着它，用户会以为开关没生效。
        _ = RequestPollAsync();
    }

    /// <summary>同步"监控本机电量"开关：关掉时不只是停止读取，还要撤掉设备条目（理由同 <see cref="DropDevicesOf"/>：留条目会带着旧电量继续显示）。</summary>
    private void ApplySystemBatterySetting()
    {
        if (_settings.MonitorSystemBattery)
        {
            _system.Start();
            // 关→开时条目已被移除，而 watcher 只在"状态实质变化"时推送：快照没变就不会自己回来（CR-66）。
            _system.Refresh();
            return;
        }

        _system.Stop();
        if (_devices.TryRemove(SystemBatteryWatcher.DeviceId, out _))
        {
            _tombstoneIds.Remove(SystemBatteryWatcher.DeviceId);
            _lowBatteryNotified.TryRemove(SystemBatteryWatcher.DeviceId, out _);
            Log.Info("已关闭本机电量监控，移除系统电量条目");
            DevicesChanged?.Invoke();
        }
    }

    /// <summary>手动触发一轮立即轮询（UI 刷新按钮）。返回的 Task 在整轮结束后完成（含 BLE 异步补读）；已有轮次在跑时复用其 Task，不静默丢弃。</summary>
    public Task RequestPollAsync()
    {
        lock (_pollSync)
        {
            if (!_currentPoll.IsCompleted) return _currentPoll;
            _currentPoll = RunExclusiveAsync(PollOnceAsync);
            return _currentPoll;
        }
    }

    /// <summary>
    /// 快速发现：让两类插件跑一遍**只认领、不轮询电量**的发现探测（不碰 BLE / 经典蓝牙）：
    /// SupportsFastDiscovery = 纯被动零写入（雷柏）；SupportsDiscoveryProbe = 要写帧但由 ProbeCooldown 兜住（迈从）。
    /// 把这类设备的发现时延从 ~60s 压到 ~15s；主轮询在跑时直接返回它。详见 docs/project/code-flow.md §2.1。
    /// </summary>
    public Task RequestDiscoveryAsync()
    {
        lock (_pollSync)
        {
            if (!_currentPoll.IsCompleted) return _currentPoll;
            if (!_currentDiscovery.IsCompleted) return _currentDiscovery;
            _currentDiscovery = RunExclusiveAsync(DiscoverOnlyAsync);
            return _currentDiscovery;
        }
    }

    /// <summary>把一段 pass 放到线程池并**串行**执行（闸门保证同一时刻只有一段 pass 在动插件）；用 Task.Run 因调用方可能是 UI 线程而 HID 探测是阻塞 I/O。</summary>
    private Task RunExclusiveAsync(Func<Task> body) => Task.Run(async () =>
    {
        try
        {
            await _passGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;   // 正在退出，不再执行
        }

        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            try { _passGate.Release(); } catch (ObjectDisposedException) { /* 退出时竞态，忽略 */ }
        }
    });

    private bool IsPluginEnabled(IBatteryPlugin plugin)
        => IsPluginEnabledByName(plugin.Name, defaultOn: false);

    /// <summary>按插件名查开关（大小写不敏感）。<paramref name="defaultOn"/> = 没有该开关时的兜底：插件缺 = 关；蓝牙监视器（ble:/btc:）无开关必须放行，否则墓碑恢复会把蓝牙设备全挡掉。</summary>
    private bool IsPluginEnabledByName(string pluginName, bool defaultOn)
    {
        foreach (var kv in _settings.PluginEnabled)
            if (string.Equals(kv.Key, pluginName, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return defaultOn;
    }

    /// <summary>设备 Id 的归属前缀（<c>{插件名小写}:</c>），用来判断条目属于哪个插件，省一套归属账本。</summary>
    private static string OwnerOf(string deviceId)
    {
        int i = deviceId.IndexOf(':');
        return i <= 0 ? "" : deviceId[..i];
    }

    /// <summary>插件被禁用后，把它此前认领的设备从列表里撤掉。`_devices` 从不移除条目、`Detach()` 又不上报状态，不显式撤就会带着旧电量继续显示（2026-10-06 用户复报）。</summary>
    private void DropDevicesOf(string pluginName)
    {
        var prefix = pluginName.ToLowerInvariant() + ":";
        var dropped = _devices.Keys
            .Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        if (dropped.Count == 0) return;

        foreach (var id in dropped)
        {
            _devices.TryRemove(id, out _);
            _tombstoneIds.Remove(id);
            _lowBatteryNotified.TryRemove(id, out _);
        }
        Log.Info($"插件 {pluginName} 已禁用，移除其 {dropped.Count} 台设备");
        DevicesChanged?.Invoke();
    }

    /// <summary>只认领、不读电量（见 RequestDiscoveryAsync）。各插件慢探测并行，Upsert 仍在本线程。</summary>
    private async Task DiscoverOnlyAsync()
    {
        try
        {
            var hidDevices = HidDeviceScanner.Scan();
            var tasks = new List<Task<IReadOnlyList<DeviceStatus>>>();
            foreach (var plugin in _plugins)
            {
                // 零写入插件（雷柏）与"写入由 ProbeCooldown 兜住"的插件（迈从）都参与；
                // 其余会写帧的插件仍只吃 60 秒主轮询，避免成倍抬高写入频率。
                if (!plugin.SupportsFastDiscovery && !plugin.SupportsDiscoveryProbe) continue;
                if (!IsPluginEnabled(plugin)) continue;
                tasks.Add(AttachSafeAsync(plugin, hidDevices, "快速发现"));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var claimed in results)
            {
                foreach (var d in claimed)
                    Upsert(d);
            }
        }
        catch (Exception ex)
        {
            Log.Error("快速发现周期异常", ex);
        }
    }

    private async Task PollOnceAsync()
    {
        try
        {
            var hidDevices = HidDeviceScanner.Scan();

            var attachTasks = new List<Task<(IBatteryPlugin Plugin, IReadOnlyList<DeviceStatus> Claimed)>>();
            foreach (var plugin in _plugins)
            {
                bool enabled = IsPluginEnabled(plugin);
                try
                {
                    if (!enabled)
                    {
                        plugin.Detach();
                        _claims.ReleaseAll(plugin.Name);
                        DropDevicesOf(plugin.Name);
                        continue;
                    }
                    attachTasks.Add(AttachLabeledAsync(plugin, hidDevices));
                }
                catch (Exception ex)
                {
                    Log.Error($"插件 {plugin.Name} 轮询失败", ex);
                }
            }

            var attached = await Task.WhenAll(attachTasks).ConfigureAwait(false);
            foreach (var (plugin, claimed) in attached)
            {
                try
                {
                    // Attach 返回值立即 Upsert：新设备即时进 UI；FreshlyClaimed 本轮 Poll 跳过。
                    foreach (var d in claimed)
                        Upsert(d);
                    foreach (var d in plugin.Poll())
                        Upsert(d);
                }
                catch (Exception ex)
                {
                    Log.Error($"插件 {plugin.Name} 轮询失败", ex);
                }
            }

            // 必须 await：以前 fire-and-forget 会让"刷新"在 BLE 侧还没跑完就返回
            await _ble.PollConnectedAsync();
            _classic.RefreshBattery();
            if (_settings.MonitorSystemBattery) _system.Refresh();
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("轮询周期异常", ex);
        }
    }

    private async Task<IReadOnlyList<DeviceStatus>> AttachSafeAsync(
        IBatteryPlugin plugin, IReadOnlyList<HidDeviceInfo> hidDevices, string label)
    {
        try
        {
            return await plugin.AttachAsync(hidDevices, _claims).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"插件 {plugin.Name} {label}失败", ex);
            return Array.Empty<DeviceStatus>();
        }
    }

    private async Task<(IBatteryPlugin Plugin, IReadOnlyList<DeviceStatus> Claimed)> AttachLabeledAsync(
        IBatteryPlugin plugin, IReadOnlyList<HidDeviceInfo> hidDevices)
    {
        var claimed = await AttachSafeAsync(plugin, hidDevices, "认领").ConfigureAwait(false);
        return (plugin, claimed);
    }

    /// <summary>立即按保留天数清理历史。设置保存后调用，也由 6 小时定时任务调用。</summary>
    public void CleanupHistoryNow()
    {
        try
        {
            var removed = _history.CleanupOlderThan(DateTimeOffset.Now.AddDays(-_settings.HistoryRetentionDays));
            // 定时任务大多数时候删 0 条，只有真删了才记一行，避免每 6 小时刷一条无信息量日志
            if (removed > 0) Log.Info($"历史清理完成，删除 {removed} 条");
        }
        catch (Exception ex)
        {
            Log.Error("历史清理失败", ex);
        }
    }

    /// <summary>
    /// 定时采样：只写"**在线且有 [采样时刻, 采样时刻 − 半个周期] 内新读数**"的设备，
    /// 离线设备保留的旧读数不再按 LastUpdated 重复落盘（CR-67）。
    /// </summary>
    private void SafeSample()
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var d in _devices.Values)
            {
                if (!d.IsOnline || !d.BatteryPercent.HasValue) continue;
                var freshAfter = now - TimeSpan.FromMinutes(Math.Max(1, _settings.SampleIntervalMinutes) / 2.0);
                if (!DeviceChangeDetector.HasFreshReading(d.LastUpdated, freshAfter, now)) continue;
                if (_lastSampledAt.TryGetValue(d.Id, out var last) && d.LastUpdated <= last) continue;   // 同一读数不写两条

                _history.Insert(DeviceCustomizationResolver.ApplyName(d, _settings.DeviceCustomizations));
                _lastSampledAt[d.Id] = d.LastUpdated;
            }
            ExpireTombstones();
        }
        catch (Exception ex)
        {
            Log.Error("历史采样失败", ex);
        }
    }

    /// <summary>本机电源独立采样节拍（5s）：定时器回调不让异常逃逸（.NET Timer 异常会 fail-fast 整个进程）。</summary>
    private void SafeSystemBatteryRefresh()
    {
        try { _system.Refresh(); }
        catch (Exception ex) { Log.Warn($"本机电源采样失败: {ex.Message}"); }
    }

    private void Upsert(DeviceStatus incoming)
    {
        // 数据源（插件/watcher）会复用并原地改写同一个 DeviceStatus 实例。直接把它存进 _devices，
        // 后续再比较就变成"同一对象和自己比"，changed 恒为假（历史即写 / DeviceUpdated / 骤降保护全失效）。
        // 所以先快照 incoming 当"新值"，库里存的始终是独立副本当"旧值"——详见 docs/project/architecture.md §6。
        var snapshot = incoming.Clone();
        var status = _devices.GetOrAdd(incoming.Id, snapshot);
        bool isNew = ReferenceEquals(status, snapshot);

        // 新设备立即通知 UI（watcher/插件首轮上报不等下一分钟轮询）
        if (isNew)
        {
            DevicesChanged?.Invoke();
            DeviceUpdated?.Invoke(status);
        }

        int? previousPercent = isNew ? null : status.BatteryPercent;
        var previousKind = status.Kind;

        bool changed = DeviceChangeDetector.HasChanged(status, snapshot);
        // 原地同步（Kind/Protocol 的"声明覆盖猜测"规则见 DeviceStatusMerger）
        DeviceStatusMerger.Apply(status, snapshot);

        // 占位条目一旦被真实来源接管（重新上线），就不再按占位规则过期剔除
        if (status.IsOnline) _tombstoneIds.Remove(status.Id);

        // 曾被用户删除的设备重新上线：撤销"隐藏"标记，此后按新设备对待（自定义信息早已清空）
        if (status.IsOnline && _settings.HiddenDeviceIds.Remove(status.Id))
            _settings.Save();

        // 新设备默认排最后：首次进入列表时追加到排序表末尾（排序与"新设备垫底"共用同一张表）
        if (isNew && !_settings.DeviceOrder.Contains(status.Id))
        {
            _settings.DeviceOrder.Add(status.Id);
            _settings.Save();
        }

        if (changed)
        {
            // 电量变化（含从离线恢复）立即写一条历史。
            // ⚠ 只写在线状态：设备掉线那一刻 LastUpdated 会前进，"在线→离线"也会被判成变化——
            // 照着写就成了"下线时刻的读数横在那里"，与 SafeSample 的采样点重叠成假平台（CR-67 实测）。
            if (status.IsOnline && status.BatteryPercent.HasValue)
            {
                _history.Insert(DeviceCustomizationResolver.ApplyName(status, _settings.DeviceCustomizations));
                _lastSampledAt[status.Id] = status.LastUpdated;
            }
            DeviceUpdated?.Invoke(status);
        }
        else if (status.Kind != previousKind)
        {
            // 类型后解析出来（Unknown → Mouse）：电量没变也要换图标，但不写历史
            DeviceUpdated?.Invoke(status);
        }

        CheckLowBattery(status, previousPercent);
    }

    /// <summary>低电量提醒的骤降保护阈值（百分点）：降幅超过它先暂缓提醒，等下次读数确认。</summary>
    private const int ImplausibleDropPoints = 20;

    private void CheckLowBattery(DeviceStatus status, int? previousPercent)
    {
        int threshold = _settings.LowBatteryThreshold;
        if (threshold <= 0)
        {
            // 关掉提醒时复位，重新开启后第一条低电量读数就能正常提醒
            _lowBatteryNotified.TryRemove(status.Id, out _);
            return;
        }
        if (!status.IsOnline) return;
        if (status.BatteryPercent is not { } percent) return;

        // 回到阈值之上（或充电/满电）→ 复位"已提醒"标记，下次跌破再提醒一次。
        // 旧实现记的是"精确百分比且永不复位"，导致 20% 提醒后 19% 又提醒（逐 1% 刷屏），
        // 而充满到 100% 再放电到 20% 反而不提醒（CR-15）。
        if (percent > threshold || DeviceStatusFormatter.IsChargingOrFull(status.Charging))
        {
            _lowBatteryNotified.TryRemove(status.Id, out _);
            return;
        }

        // 骤降保护：键盘休眠/关机瞬间的伪读数（1%/0%）会让电量瞬间"掉"到阈值以下。
        // 真掉电不可能一次掉 20 个点，所以这种降幅先不提醒，等下次读数确认。
        if (DeviceChangeDetector.IsImplausibleDrop(previousPercent, percent, ImplausibleDropPoints))
        {
            Log.Warn($"低电量提醒暂缓: {status.DisplayName} {previousPercent}%→{percent}% 降幅异常，等下次读数确认");
            return;
        }

        // 跨过阈值只提醒一次（TryAdd 兼任"未提醒过"的判定，天然幂等）
        if (_lowBatteryNotified.TryAdd(status.Id, true))
        {
            LowBatteryDetected?.Invoke(status);
            Log.Info($"低电量提醒: {status.DisplayName} {percent}%");
        }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _sampleTimer?.Dispose();
        _discoveryTimer?.Dispose();
        _cleanupTimer?.Dispose();
        _systemBatteryTimer?.Dispose();
        foreach (var p in _plugins)
        {
            p.StatusPushed -= Upsert;
            p.Detach();
        }
        _ble.Dispose();
        _classic.Dispose();
        _system.Dispose();
        _passGate.Dispose();
    }
}
