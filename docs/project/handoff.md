# 会话交接（给下一个 AI / 未来的自己）

> 快照时间：2026-10-14。**先读本文，再按需查其他文档**；内容冲突时以专题文档为准（本文是快照）。
> 逐轮原因在 **[changelog.md](changelog.md)**。本文只留当前状态、设备、未完成的待办、注意事项、文件速查。
> **维护约定**：收工后本轮原因追加进 `changelog.md`；本文件只改 §1 状态与 §4 未完成待办。已完成的待办删掉，不划掉留着。全文超过 120 行先删历史再写（`AGENTS.md` §3）。

## 1. 当前状态

当前版本 **1.2.2**（本机便携 zip，未发版；`dist/` 里 1.2.0 为本机试装用；正式线上以 GitHub Release 1.2.1 为准，Gitee 是回退源）。更新源：GitHub 优先、8 秒不通回退 Gitee（**尚未发版**，1.2.0 及以前只走 Gitee）。发新版时留着上一版 `*-full.nupkg`，才会有 delta；两个仓库都要传同一份资产，规则见 `dev/build.md` §8。

本机已打通：迈从 G87（0x13）、迈从 G3 A（纯被动 `AA FA AE`，零写入）、雷柏 VT3（status-report7，纯被动）、北通 T9（被动流）、经典蓝牙耳机（HFP 网关节点 `DEVPKEY_Device_BatteryLevel`）。罗技 Bolt 接收器（C548）+ MX Master 3S 已真机验证。本机电量可选，默认关。弱水时砂 2.4G 无电量通道，不再投入。

任务栏贴片是原生子窗口 `Services/TaskbarPillWindow.cs`（`SetParent` 进任务栏），不是 WinUI 覆盖窗。现由 `Services/TaskbarPillManager.cs` 按设置选屏：只贴主屏或指定副屏（`Shell_SecondaryTrayWnd`），支持 1–6 台设备单行/两行排布，拖动位置记忆。小组件仍用 `TransparentWindowSurface`；Win10 上因此没有 DWM 投影（BACKLOG **CR-57**，未做）。旧覆盖窗的白框、裁字、抢 Z 序在新贴片上已不存在。原理见 `dev/runtime-pitfalls.md` §16；§17 只对小组件。

页面缓存：设备页、设置页、关于页 `Required`；历史页、统计页改为 `Disabled`（离开即释放，降低常驻视觉树）。订阅跟 `Loaded`/`Unloaded`，注意关于页在构造函数里订阅静态事件且无解绑路径，必须保持常驻。设置页可搜索。日志按日：`logs\app-日期.log`。插件开关即写入授权，没有第二套「写入许可」。

设备列表支持右键自定义名称、图标与强调色（16 色预设），支持上移/下移排序与删除离线设备；顺序持久化在 `DeviceOrder`，删除记录在 `HiddenDeviceIds`（防止历史记录复活），按设备 ID 持久化。更新方式三档（自动/仅通知/禁用）在关于页，存 `AppSettings.UpdateMode`。

**同一物理设备的多通道合并**：一只设备同时有 2.4G 与蓝牙时，两条记录可合并成一行（`AppSettings.DeviceMergeGroups`，组合 ID = 成员 ID 排序后以 `+` 连接）。纯逻辑在 `Models/DeviceMergeResolver.cs`，历史聚合在同名入口 `Storage/HistoryMergeResolver.cs`。设备页、小组件、任务栏贴片、托盘、历史页、统计曲线共六个展示面都只读这一个视图。规则：电量/名称取第一个在线通道，任一通道在线即整行在线，徽标逐通道标注（离线灰）；**组内有在线蓝牙时 2.4G 通道被压制**（`MergedDevice.Suppressed`，灰徽标 + 副标题「已切走，不采信」），不参与取值；**外观（名称/图标/颜色）整组共用一份**（`MergedCustomization`）：合并时以「合并到」的目标设备为准覆盖主动方，之后每次编辑都摊给组内所有成员，换通道不跳；同名设备自动弹一次合并提示，右键可拆分。共用的强调色入口是 `UiPalette.Accent`（离线灰 → 自定义色 → 电量状态色）。

审查标准在 `docs/dev/review-standard.md`，未修项全集在 `docs/reviews/BACKLOG.md`（不要在本文再抄一份）。

下结论前先跑 `cmd /c ver`。**本机现在是 `10.0.26200.9445`（Win11 25H2）**——文档曾记成 Win11，2026-10-12 复核为 Win10 19045.6456，2026-10-14 再查已升到 26200。所以「哪一层背景可见」「Win10 上透明与 DWM 投影互斥」（CR-57）这类基于 Win10 的判断都要按新系统重判，别照抄。

## 2. 用户设备清单（勿对号入座错）

| 设备 | 通道 | 状态 |
|---|---|---|
| 迈从 G87（VID_41E4&PID_2001） | 0x13 会话 | 已打通 |
| 迈从 G3 A（VID_A8A5&PID_2255） | `mi_02` 纯被动 `AA FA AE` | 已打通，零写入；只在开机时推一帧，故全程不判离线（protocols.md §7） |
| 英菲克 M1 Pro（蓝牙通道） | BLE GATT | 已读数；其 2.4G 接收器是 `1EA7:0064`，静默不推帧，待抓包（protocols.md §8） |
| 罗技 Bolt（C548 + MX Master 3S） | HID++ | 已真机验证 |
| 雷柏 VT3 二代（VID_24AE&PID_1412） | status-report7 被动心跳 | 已打通，零写入。雷柏不是 HID++，勿让 Logitech 插件认领 |
| 英菲克 ZHEN（VID_1D57&PID_FA65） | report 0x03 被动状态帧 | 已打通，零写入；放电 `0x01` / 充电 `0x03` 已校准（protocols.md §6） |
| 北通 T9（PID_5073）/ 鲲鹏20（PID_5191） | 厂商接口 0xFF00；T9 走 Col04 被动流 | T9 已真机验证；鲲鹏20 主动查询未验证 |
| 弱水时砂 2.4G（38FF:0001） | — | 无电量通道（protocols.md §3），不再投入 |
| EDIFIER W820NB | HFP 网关 `DEVPKEY_Device_BatteryLevel` | 已打通，与设置页一致 |
| 华硕 AURA、Virtual Multitouch、lifeme 音箱 | — | 与电量无关，勿探测 |
| 本机电池 | `GetSystemPowerStatus`，Id `sys:battery` | 已接入，默认关。托盘最低电量不统计本机 |

## 3. 历史

不在这里记。查「为什么这么改」看 [changelog.md](changelog.md)；查协议、坑、流程看对应专题文档。

## 4. 待办

审查未修项以 `docs/reviews/BACKLOG.md` 为准。这里只留还要人去做的事。

1. **迈从充电标志**：插上充电线，看日志里的状态字节，补全 `ParseKeyboardV1Charge`（已知 0x10 位 = 充电中）。
2. **雷柏充满**：放电 0x01、充电 0x02 已校准。充满时 `[7]` 还没采到，采到再补映射。
3. **迈从 QHW**（3837:3033）已实现，遇到再验证。
4. **北通鲲鹏20**（PID_5191）主动查询未真机验证。T9 的 `byte23` 低位仍只认 `0x50` = 未连接；验证方法见 protocols.md §4.4.1。
5. **贴片人手复看**：副屏选屏与拖动记忆（在副屏 DPI 不同、任务栏自动隐藏、竖向任务栏下各看一眼）、深色对比（CR-42）、是否压住任务栏按钮（CR-39）、右键菜单（CR-58/CR-60）、悬停设备名（多台/两行/底栏/深色任务栏）。分辨率往返已于 2026-10-13 通过。Win10 小组件投影（CR-57）等用户决定是否做。
6. **英菲克 M1 Pro 的 2.4G**：接收器 `1EA7:0064`（产品名 `2.4G Mouse`）只暴露 vendor `0xFFB5`（in/out 各 8 字节）与标准鼠标通道（被系统独占）。vendor 通道可只读打开但**不主动推帧**，被动路径走不通。下一步需 USBPcap：`sc config USBPcap start= system` 后**重启**，才有 `\\.\USBPcapN` 节点，再用 `scripts/capture-usb.ps1` 抓主机查询帧。勿盲发帧（safety.md）。
7. **CR-53 真机**：迈从键盘睡够 3 分钟再唤醒，日志应在 1 分钟内出现重新上线。表在 `Plugins/WriteBackoff.cs`。
8. **页面缓存真机**：来回切换是否变快、离开再回来数据是否仍刷新、设置值是否与 `settings.json` 一致、搜索是否仍可用。坑见 `dev/runtime-pitfalls.md` §19。相关未修项 CR-61 / CR-62 / CR-63。
9. **启动更新确认框**：本轮只验到"托盘气泡 + 日志发现新版本"（`StartMinimized=true`，窗口没开）。打开主窗口应弹「发现新版本」确认框，未真机确认。
10. **Win11 26200 复核**：本机已升到 26200（见 §1），CR-57（小组件自绘投影）等基于 Win10 的结论需按新系统重判；任务栏贴片在 Win11 上的表现（CR-39/41/42/48）也值得重看。
11. **迈从 `3554:FA07` 误认领**：G75 Pro 的 dongle 多出一条无产品名的 vendor 接口，被 `MchosePlugin`（无 VID 白名单）当成独立键盘。短期靠手动合并；长期需给它加 VID 白名单（待日志确认接口形态，见 protocols.md §1.6）。

## 5. 给下一个 AI 的注意事项

1. **开工**：`docs/README.md` 症状速查 → 本文件 → 专题文档。文档比记忆新。
2. **硬件**：绝不盲扫；只发有出处的帧。插件开关就是写入授权（`PluginEnabled[Name]`）。熔断和退避在基类里，子类别绕过。详见 `hardware/safety.md`。
3. **dongle 静音**：本应用、浏览器里的 M HUB、官方 HUB 谁持有会话谁工作。崩溃进程会把会话粘死。先看 safety.md §2，别急着改协议。
4. **调试**：抓官方软件的包（debugging.md §1）；验证用 `pip install hidapi`（§2）。
5. **构建**：用 sln，从 `bin\x64\...\win-x64\` 启动。应用在跑时先结束进程，否则发布会锁文件。见 `dev/build.md`。
6. **启动方式**：工具拉起的进程会被进程树清掉。用 `explorer.exe "VigilWatt.App.exe"`。强杀会按 safety.md §2 占死 dongle 会话。
7. **用户偏好**：中文交流、文档中文；界面只中英文；个人自用，不做游戏内显示、HTTP API、账号。
8. **雷柏**：停手 = dongle 静音，静音不是离线。离线主判据是 `[18].bit7`，走约 6 秒确认，不要改成立刻离线。见 protocols.md §3。
9. **插件配置分三层**：协议事实和安全声明必须按品牌；离线策略集中在 `OfflinePolicy`。`CountPollFailures` 与 `SupportsPassiveListen` 是两件事，合成一个布尔会让迈从关机后永远不离线。
10. **发现节拍**：15 秒节拍只给 `SupportsFastDiscovery`（零写入）或 `SupportsDiscoveryProbe`（`ProbeCooldown` 兜住写入）。会写帧的新插件先看 safety.md。
11. **日志**：自研 `Log`，不引 Serilog。默认 Info；逐帧用 Debug。按日文件、默认留 14 天。设计见 `dev/logging.md`。
12. **脚本**：一次性探针放 `scripts/`，用完即删。长期 C# 项目放 `src/tests/`。仓库根不放临时文件。

## 6. 关键文件速查

| 要动什么 | 去哪 |
|---|---|
| 迈从 / 雷柏 / 北通协议 | `Plugins/Mchose/`、`Plugins/Rapoo/`、`Plugins/Betop/`（规格在 protocols.md） |
| 离线判定 | `Plugins/OfflinePolicy.cs` + `BatteryPluginBase` 的 `MarkOffline` / `RequestOffline` / `RequestConfirm` |
| 写入授权 | `AppSettings.PluginEnabled`；基类 `WritesEnabled`。设置页 `AddPluginRow` |
| 新增品牌 | 新建插件继承 `BatteryPluginBase`。会写帧的默认关 |
| 状态文案与充电 | `Models/DeviceStatusFormatter.cs`、`Models/ChargingStateMap.cs`；色值在 `Services/UiPalette.cs` |
| 变化判定 | `Devices/DeviceChangeDetector.cs`。`Upsert` 必须先 `Clone()`，不要存共享可变实例 |
| 设备排序 / 删除离线 | `DeviceManager.MoveInOrder` / `RemoveOfflineDevice`；持久化 `AppSettings.DeviceOrder` / `HiddenDeviceIds` |
| 设备颜色 | `Settings/DeviceCustomization.ColorHex`；取色 `DevicePresentation.AccentColor`；色板 `UiPalette.CustomColorChoices` |
| 轮询 / 采样 | `Devices/DeviceManager.cs`（code-flow.md） |
| 经典蓝牙 | `Ble/BluetoothClassicWatcher.cs`、`Ble/ClassicDevNodeReader.cs` |
| 本机电量 | `Power/SystemPowerReader.cs`、`Power/SystemBatteryWatcher.cs`。托盘在 `TrayService.Render` 里跳过 `DeviceSource.System` |
| 任务栏贴片 | `Services/TaskbarPillManager.cs`（选屏/句柄缓存）、`TaskbarPillWindow.cs`、`TaskbarPillRenderer.cs`、`TaskbarLayout.cs`；显示器枚举 `MonitorCatalog.cs`。原理 runtime-pitfalls.md §16 |
| 更新方式 | `Services/UpdateMode.cs`；启动流程 `App.CheckForStartupUpdateAsync`；入口在 `Views/AboutPage` |
| 小组件透明 | `Services/TransparentWindowSurface.cs`。贴片不用它。见 §17 |
| 设备图标 | `Services/DeviceIconFactory.cs`（不要用字体图标） |
| 程序图标 | `scripts/make-app-icon.py` → `Assets/VigilWatt.ico`；csproj `ApplicationIcon` 和 `MainWindow.ApplyWindowIcon` 都要 |
| 历史库 | `Storage/HistoryStore.cs`；页面 `Views/HistoryPage`、`Views/StatsPage` |
| 日志 | `Utils/Log.cs`；设置页「日志」；设计 `dev/logging.md` |
| 语言包白名单 | `VigilWatt.App.csproj` 的 `MuiLanguagesToKeep`（build.md §6） |
| 某轮为什么这么做 | `docs/project/changelog.md` |
| 还有什么没修 | `docs/reviews/BACKLOG.md` |
| 审查红线 | `docs/dev/review-standard.md` |
