# 架构

> 目标读者：要改这个项目的人（或 AI 助手）。讲清**模块边界、数据怎么流、线程怎么摆、扩展点在哪**。
> 具体协议细节见 [PROTOCOL.md](PROTOCOL.md)，传感器来源见 [SENSORS.md](SENSORS.md)。

---

## 1. 一张图看懂

```
                    ┌─────────────────────────────────────────────┐
                    │              OpenMIFS.exe (单文件)           │
                    │                                             │
  BIOS / EC ──WMI──▶│  Mifs       功能号读写（性能模式/锁/背光…）   │
                    │                                             │
  PDH ─────────────▶│  Sensors    ReadAll() → List<Reading>        │
  WMI ─────────────▶│    ├ PrimeAll()   计数器预热（一次性）        │
  atiadlxx.dll ────▶│    ├ AdlPmlog     AMD 官方遥测（温度/功耗/频率）│
  物理盘 IOCTL ────▶│    └ 磁盘温度三级兜底                         │
                    │                                             │
                    │  MainForm   ┌─ 状态面板（键值行）             │
                    │             ├─ 传感器面板（键值行，可缩放）    │
                    │             └─ 托盘提示设置（勾选/阈值）       │
                    │                                             │
                    │  TrayContext ──▶ TrayText（悬停提示文案）     │
                    │             └─▶ TrayIcon（把数值画进图标）    │
                    │                                             │
                    │  Osd / OsdOverlay   官方 OSD 诊断 + 自带提示  │
                    │  Startup            计划任务（带 --tray）     │
                    │  Settings / Log     settings.txt / openmifs.log│
                    └─────────────────────────────────────────────┘
```

三者（exe / `mifs-gui.ps1` / `mifs.ps1`）**互不依赖**，但共享同一套协议常量、同一份日志文件、
同一套数据来源策略。改协议语义时**三处要同步**。

---

## 2. 模块职责

### 2.1 `OpenMIFS.cs`（托盘 exe，约 3600 行）

| 模块 | 职责 | 关键点 |
| :--- | :--- | :--- |
| `Mifs` | 唯一的硬件写入口 | `MiInterface(InData[32]) → OutData[30]`；`InData[1]`=250 读/251 写；**必须用实例对象调用**（类级调用必失败）；功能号常量与 `AcTypeName()` 也在这 |
| `ModeMap` | 性能模式值 ↔ 标签 | **本机值编码与上游驱动文档相反**，改机型时先 `bench` 校准 |
| `Caps` | 能力探测缓存 | `capabilities.txt`；风扇满速的结论**与当时供电类型绑定**（`fanboost_actype`） |
| `Settings` | `settings.txt` 键值读写 | 现有键见 AGENTS.md §7 |
| `Log` | 日志 | `%LOCALAPPDATA%\OpenMIFS\openmifs.log`，>1 MB 轮转；三种形态共用 |
| `Proc` | 安静调外部命令 | `schtasks` / `sc` / `taskkill` 的 stderr 在 `$ErrorActionPreference='Stop'` 下会炸，这里统一吞掉并返回 exit code |
| `Startup` | 计划任务 | `/RL HIGHEST` 免 UAC；**命令行必须带 `--tray`**；`RepairIfNeeded()` 用 `/XML` 检查老任务并重建 |
| `Osd` / `OsdOverlay` | 官方 OSD 诊断、自带屏幕提示 | 自带提示在 `auto` 模式下会**让位**给官方 OSD（等 350 ms 看 `FloatingNativeWindow` 是否可见） |
| `MainForm` | 界面与刷新调度 | 键值行渲染（`KvRow`）、`SensorOrder` 决定阅读顺序、窗口可缩放 |
| `TrayContext` | 托盘图标/菜单/定时刷新 | 5 秒一轮；`--tray` 模式只驻留托盘 |
| `TrayText` | 悬停提示文案 | 白名单 + **最坏宽度预算**（详见 TRAY-TOOLTIP.md）；维护值快照 |
| `TrayIcon` | 把数值画进托盘图标 | 阈值可配置；GDI 句柄必须释放；尺寸取 `max(16×dpi/96, SM_CXSMICON)` 且下限 20 |

### 2.2 `Sensors.cs`（传感器层，约 1200 行）

统一产出 `Reading { Group, Name, Value, Note, Ok }`，界面**只认这个结构**，不关心数据来源。

| 子模块 | 说明 |
| :--- | :--- |
| `PrimeAll()` | PDH `Power` 这类**速率计数器**必须先创建再采一次，否则永远读 0（实测 0.00 W → 24.82 W） |
| `ReadAll()` | 按 CPU → 温度 → GPU → 内存 → 存储 → 电池 → 风扇 的顺序取数 |
| `AdlPmlog` | AMD 显卡驱动自带的用户态 DLL `atiadlxx.dll`：`ADL2_New_QueryPMLogData_Get` → 256 个传感器槽。**核显/CPU die 温度、ASIC 功耗、GFX/显存/CPU 频率都从这里来**（= Adrenalin「性能 → 指标」同源） |
| 磁盘温度三级兜底 | WMI `MSFT_StorageReliabilityCounter` → `IOCTL_STORAGE_QUERY_PROPERTY`（属性 **52/51**）→ NVMe 健康日志（log page 0x02，经 StorPort 适配器）。**属性 ID 有跳值，见 AGENTS.md §3** |
| `Render(list)` | 渲染成 `键: 值` 文本；`MainForm` 与托盘共用同一份解析逻辑 |

**扩展一个新传感器**：在 `ReadAll()` 里加一个 `Reading`，在 `MainForm.SensorOrder` 里加名字
（决定顺序），再按需在 `TrayText.All` 里加白名单项（只有"最坏宽度放得下"的才加）。

---

## 3. 数据流

### 3.1 界面（3 秒一轮）

```
_timer.Tick → RefreshAll()
   ├─ Mifs 读状态（模式/锁/背光/风扇/供电/OSD/自启）
   ├─ sb 拼 "键: 值" 文本
   ├─ SetRows(_pnlStatusRows, …)        渲染状态面板
   ├─ TrayText.CaptureStatusText(sb)    更新托盘快照（模式/风扇/版本）
   └─ RefreshSensors()  ──▶ 线程池
          ├─ Sensors.ReadAll() → Render()
          ├─ BeginInvoke 回 UI 线程
          │    ├─ SetRows(_pnlSensorRows, …)
          │    └─ TrayText.CaptureSensorText(text)   更新托盘快照（CPU 四项/电池）
          └─ 首次读取 1~2 秒（PDH 预热 700 ms）
```

### 3.2 托盘（5 秒一轮）

```
_trayTimer.Tick → RefreshTray()
   ├─ 若窗口隐藏且（提示项 或 图标）需要传感器 且快照 >8 秒
   │      → MainForm.RequestHiddenSensorSnapshot()   （只采快照、不碰控件）
   ├─ TrayText.Build()        按白名单+预算拼悬停提示（≤62 字符）
   └─ TrayIcon.Update(_tray)  值或档位变了才重画
```

> **注意**：`RefreshSensors()` 首行是 `if (!Visible) return;` —— 窗口收进托盘后界面不再刷新。
> 因此托盘的数据必须走 `RequestHiddenSensorSnapshot()`，别指望窗体。

### 3.3 关窗 / 退出

```
关闭窗口 → e.Cancel=true → HideToTray()（程序继续后台运行；首次提示"仍在后台"）
托盘菜单退出 → ExitApp() → 停表 → 释放 NotifyIcon → ForceClose()
```

单实例：`Global\OpenMIFS_SingleInstance` 互斥体；`--tray` 撞车时**静默退出**（不弹框打扰）。

---

## 4. 线程模型

| 线程 | 干什么 | 注意事项 |
| :--- | :--- | :--- |
| UI 线程 | 所有控件、托盘、定时器 | 传感器读取**绝不能**放这里（首次 1~2 秒会卡住窗口） |
| 线程池 | `Sensors.ReadAll()` | 用 `_sensorBusy` 互斥；结果必须 `BeginInvoke` 回 UI |
| PDH / WMI 内部 | 计数器与查询 | 速率计数器有最小采样间隔，`PrimeAll()` 里统一预热 |

控件句柄：`--tray` 模式虽然不显示窗口，但**必须让窗体创建句柄**（`BeginInvoke` 依赖它），
做法是"最小化 + 不在任务栏 + Opacity=0 地 `Show()` 一次，然后立刻 `HideToTray()`"。

---

## 5. 扩展点

| 想加什么 | 怎么做 |
| :--- | :--- |
| 新机型支持 | 先 `mifs.cmd probe` + `mifs.cmd bench` 校准功能号与模式值编码，把结果写进 `docs/TESTED-MODELS.md` |
| 新托盘提示项 | 在 `TrayText.All` 加一项（**必须填最坏宽度**），并在 `CaptureStatusText/CaptureSensorText` 里写快照 |
| 新托盘图标数据源 | `TrayIcon.Kinds` / `KindLabels` 加一项，`Number()` 里给档位划分规则 |
| 新传感器 | 见 §2.2 末尾 |
| 新 CLI 动作 | `mifs.ps1` 的 `switch ($Action)` **和** `ValidateSet` 两处都要加（CI 会检查一致性） |
| 新设置项 | `Settings.Get/Set`，键名写进 AGENTS.md §7 与 README |

---

## 6. 已知的"反直觉"设计（别当成 bug 改掉）

| 现象 | 原因 |
| :--- | :--- |
| 传感器行名字与面板顺序不一致 | 顺序由 `MainForm.SensorOrder` 决定，`ReadAll()` 只负责产出 |
| 关掉窗口程序还在跑 | 设计如此（托盘常驻），退出走托盘菜单 |
| 风扇满速按钮被禁用且写"Type-C 供电下被禁用" | 电源类型门控，不是 bug（见 FAN-CONTROL.md） |
| 托盘提示放不下新项、勾不上 | 最坏宽度预算保护（见 TRAY-TOOLTIP.md） |
| 顶部不再出现"MIFS CPU 温度未实现" | 那两项指功能号 22/23；面板的 CPU 温度来自 AMD 通道，写出来会误导 |
| `capabilities.txt` 里风扇满速结论带供电类型 | 同一机型换电源后结论会变，缓存必须绑定供电类型 |
