# scripts/ — 构建、发布与调试脚本

> 本目录放构建/发布流程脚本与一次性 / 临时调试脚本（PowerShell / Python / 一次性 P/Invoke 探针）。
> **长期存在的 C# 项目不放这里**：测试项目在 [`src/tests/`](../src/tests/)，工程源码在 [`src/`](../src/)。
> 临时产物用 `%TEMP%`，仓库根目录只放工程文件。新增脚本后在下面补一行索引。

## 脚本索引

| 脚本 | 用途 |
|---|---|
| `build-installer.ps1` | 打安装包：`dotnet publish`（Release x64 self-contained）→ 读发布 exe 的 FileVersion 当版本（截 3 段）→ `vpk pack`。产物落 `dist/`（Setup.exe、Portable.zip、`releases.win.json`、`RELEASES`、`*.nupkg`）。发新版**别清 `dist/`**，留着上一版全量包才有 delta；详见 `docs/dev/build.md` §8 |
| `publish-gitee.ps1` | 将 `dist/` 全部打包产物作为当前版本的 Gitee Release 附件发布；需要已推送的 `v<版本>` 标签与 Gitee 写权限令牌（`-Token` 或 `GITEE_TOKEN`，**脚本不读令牌文件**；本机令牌在仓库根 `.vpk-github-token`）。完整发版序列与三个 Gitee 特有坑见 `docs/dev/build.md` §8 |
| `code-fingerprint.py` | **代码指纹校验器**（纯标准库）：剥掉注释与空白后对代码 token 序列算 sha256，用来**证明"只改了注释、逻辑一行没动"**。`snapshot <out.json>` 记基线 / `verify <in.json>` 比对（`-xaml` 后缀对 `.xaml` 生效，剥 `<!-- -->` 后比空白归一化文本）。做注释精简、批量重排这类"只该动注释"的改动前先 `snapshot`，改完 `verify` 必须输出 `[OK]` |
| `make-app-icon.py` | **程序图标生成器**（纯标准库，无需 Pillow）：解析式绘制绿色圆角方块 + 白色闪电，超采样抗锯齿，输出 `src/VigilWatt.App/Assets/VigilWatt.ico`（16~256 共 9 档）。`--preview x.png` 出一张放大预览图便于肉眼比对。**改图标配色/字形改这个脚本后重跑即可，不要手改 .ico** |
| `probe-devnode-battery.py` | **PnP 设备节点电量探针**（纯 ctypes + SetupAPI）：枚举全机节点读 `DEVPKEY_Device_BatteryLevel`。经典蓝牙耳机电量就是这么找到的（EDIFIER W820NB 挂在 HFP 网关节点上）；`--all --match X` 可 dump 匹配节点的全部属性，查任何"Windows 能看到但代码读不到"的设备属性都用它 |
| `probe-process-memory.py` | **进程内存分层采样**（纯 ctypes、只读）：按 `VirtualQueryEx` 的 `Type` 分层统计 **PRIVATE / MAPPED / IMAGE**，并附工作集、提交大小、句柄数。位置参数：`<进程名或PID> [秒数] [输出文件] [间隔秒]`。用来区分**"私有提交泄漏"（真泄漏，PRIVATE 单调涨）**与**"工作集可回收驻留"（高水位，回收后 PRIVATE 不动）**——判据见 `docs/reviews/archive/memory-growth-2026-10-12.md` §6。**注意：已提交虚拟内存总量 ≠ 任务管理器「提交大小」**，IMAGE 与文件后备 MAPPED 不计入进程提交量 |
| `parse-minidump.py` | **minidump 内存布局解剖**（纯标准库，无需 WinDbg）：解析 stream 3/4/9/12/16/17，给出进程时长、句柄/模块/线程数、dump 实抓量、分层提交（PRIVATE/MAPPED/IMAGE）与最大区域。**核心判据：dump 文件大小 ≠ 进程占用**（本机 725 MB 的 dump 里 181 MB 是 NVIDIA 驱动 DLL）。另有 FILETIME 时区坑：必须走 UTC 再本地化 |
| `probe-classic-kind.py` | **经典蓝牙设备类型信号探针**（纯 ctypes + SetupAPI）：枚举 `BTHENUM\*` 节点，打印服务 UUID（实例 ID 前缀）、`DEVPKEY_DeviceDisplay_Category`（如 `Input.Mouse`）与 ClassGuid。用来回答"这台经典蓝牙设备到底是耳机还是鼠标"，`--all` dump 全属性 |
| `probe-hid-interfaces.ps1` | **HID 接口清单探针**（纯 P/Invoke，只读）：枚举全机 HID 接口的 VID:PID / usage / 报文长度 / report ID / 产品名，`-Vid` 可只列某一家。用来查"设备插着但插件没认领"——先确认接口到底存不存在、元数据对不对得上插件的 `IsCandidate`。零写入 |
| `probe-hid-passive.ps1` | **HID 被动监听探针**（纯 P/Invoke，只读）：只以 `GENERIC_READ` 打开指定接口，把收到的 input report 连时间戳和间隔一起打出来。用来判断"这台 dongle 会不会主动推电量帧"——沉默 30 秒（含操作设备）就说明只能主动查询，零写入路径走不通。`-Interface col01` 选 collection，默认优先 vendor 通道 |
| `test-rapou7.ps1` | 雷柏 dongle（VID_24AE）HID P/Invoke 直测。也是 build.md §7"PowerShell 5.1" 的范例：本机 Add-Type 不支持字符串插值/内联 out/可空注解，要用老式语法 |
| `capture-usb.ps1` | **USBPcap 抓包助手**（需先装 Wireshark + USBPcap 驱动并以管理员运行）：`-Scan` 逐个根集线器抓 5 秒，按文件大小找出设备挂在哪个 `\\.\USBPcapN`；`-Hub N -Seconds S -Out x.pcap` 正式抓。用来复现官方驱动的真实帧——网页驱动的帧往往只在抓包里才看得到。**注意 USBPcap 是 USB 类 UpperFilter，装完必须重启并 `sc config USBPcap start= system` 才会出现设备节点** |
| `probe-mchose-g3a.ps1` | **迈从 G3A（A8A5:2255）单帧验证探针**：遍历 `mi_01` 各 collection 与 `mi_02`，尝试发送官方抓到的电量查询帧 `13 4A 01 …`（20 字节，带校验），逐条打印 `HidD_SetOutputReport` 的 Win32 错误码。**结论见 `docs/hardware/protocols.md` §7：协议与 keyboard-v1 一致，但官方走接口级控制传输，Windows 用户态发不出去** |
| `test-rose-dongle-interactive.py` | ROSE dongle（38FF:0001）交互式被动监听 5 分钟，零写入——耳机按键操作会经 dongle 出现在这里，可当"耳机已连接"探针 |
| `test-rose-dongle-passive.py` | ROSE dongle MI_03（consumer）被动监听 60 秒，零写入 |
| `test-rose-headset-getinfo.py` | FIRST ANGEL USB 直连官方 GET_INFO 查询（report 0xAB，帧格式来自官方升级工具反编译），纯只读 |
| `test-rose-headset-passive.py` | FIRST ANGEL USB 直连 MI_03 被动监听 60 秒，零写入——看耳机是否自发推 0xAB 帧 |

## 约定

- **零写入原则**：协议未摸清前只做被动监听；主动写帧前先读 `docs/hardware/safety.md`（硬件安全红线）。
- Python 脚本依赖 `pip install hidapi`（HID 直测手法见 docs/hardware/debugging.md §2）。
- PowerShell 脚本必须存成 **UTF-8 with BOM**，否则 5.1 按 GBK 解析中文会报假语法错误（见 skill `powershell-51-compat` §五）。
- 脚本里的设备路径/接口号是本机实测值，换设备后以日志（按日 `app-日期.log`）的"HID 元数据"行为准。
- 排查完的脚本：能沉淀成知识的写进 `docs/`，脚本本身可删；值得留的再进索引。
