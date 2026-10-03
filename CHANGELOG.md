# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.3.2] - 2026-10-03

### 变更

- **数据不再用文本框显示，改成键值行**（用户要求："别搞这些文本栏，只要 CPU: xx ℃ / 功耗: xx W / 频率: xx GHz 这种"）
  - 灰色小标签 + **加粗大字数值**，一行一个指标，冒号对齐
  - 读取不到的值显示灰色 `未实现` / `需要管理员`，一眼能分辨
  - 数据源说明（PDH 计数器路径、估算公式、ACPI 热区实例名等）移入**鼠标悬停提示**，不占版面
  - 「状态」区同样改成键值行；与传感器重复的 MIFS CPU 温度/功率行已移除（只在顶部"未实现"清单里提一句）
- **阅读顺序固定为**：CPU 温度 → CPU 功耗 → CPU 频率 → CPU 负载 → GPU → 内存 → 磁盘 → 风扇 → 电池
- 细节收进提示、不占行：每核功耗、VDDCR/SoC、插槽功耗挂在「CPU 功耗」的悬停提示里；
  电池的满充/设计容量挂在「电池」的提示里
- 「诊断 OSD」「探测数据源」不再往面板里灌长文本：结果写文件，弹窗问你要不要用记事本打开

## [0.3.1] - 2026-10-03

### 变更

- **界面重做：取消选项卡，传感器直接显示在首页，整窗可缩放拖动**
  - 左列 = 硬件控制（性能模式 / 风扇 / 硬件开关 / 键盘背光 / 启动与 OSD），固定宽度
  - 右列 = 「状态」+「传感器」两个面板，**随窗口一起变大**（锚定 Top|Bottom|Left|Right）
  - 窗口改为可拖动缩放：`FormBorderStyle.Sizable`，默认 940×660，最小 700×560
  - 默认尺寸下传感器面板可滚动；把窗口拉大就能一屏看全
    （实测拉到 1250×860：CPU 功耗/频率/负载/每核/供电域 + 温度 + GPU + 内存 + 存储 + 风扇/电池 全部可见，无需滚动）
  - 底部加提示「窗口可拖动缩放，右侧数据区会跟着变大」
  - 起因：用户反馈"找不到传感器入口"——原来做成选项卡，标签又小又像面板标题
- 「诊断 OSD」的结果现在直接显示在首页右侧的「状态」框里，不再需要切页

## [0.3.0] - 2026-10-03

### 新增

- **传感器面板（零驱动）**：exe 与 `mifs-gui.ps1` 新增「传感器」选项卡，命令行新增 `sensors` / `sensors probe`
  （三种形态共用同一套读数与判定逻辑）
  - CPU：**封装功耗（AMD RAPL）**、每核功耗、SoC/VDDCR 供电域、插槽功耗 —— PDH `\Energy Meter(*)\Power`（mW）
  - CPU：有效频率（估算）、负载 —— PDH `\Processor Information(_Total)`
  - 温度：**热区 / 封装邻区温度**、高精度温度、**降频原因位域** —— PDH `\Thermal Zone Information(*)`（K / 0.1K）
  - GPU：按引擎的利用率、专用显存占用 —— WMI `Win32_PerfFormattedData_GPUPerformanceCounters_*`
  - 内存：容量/类型/速率/占用；存储：型号/介质/总线/健康/**温度**（需管理员）/磨损/通电时长
  - 风扇（MIFS）、电池（电量/供电/健康度）
  - `sensors probe` 逐项报告本机哪些传感器通道可用，结论可用于其它机型适配
  - 新增 `OpenMIFS.exe --sensors`：无界面探测，输出 `%LOCALAPPDATA%\OpenMIFS\sensors-probe.txt`
- 新增 [docs/SENSORS.md](docs/SENSORS.md)：可行性分析、**单位标定实测**、踩坑清单、被排除的方案

### 关键实现记录（都是实测踩出来的）

- **PDH 速率类计数器必须先预热**：`\Energy Meter(*)\Power` 等第一次读会返回 0。
  做法是一次性建好全部计数器、各采一次，再统一等 700 ms 采样窗口，之后才有真值。
- **单位不能猜**：`Energy Meter\Power` 是**毫瓦**（本机空闲 10773 → 8 线程满载 35757，即 10.8 W → 35.8 W）；
  热区 `Temperature` 是**开尔文**、`High Precision Temperature` 是**0.1 K**。
- **不要用累计的 `Energy` 计数做差分算功率**：单精度浮点 + 从开机累计，11 秒的差值会被舍入误差淹没。
- **GPU 百分比要用 WMI 的「已格式化」类**：`PerformanceCounterCategory.ReadCategory()` 只给原始值，
  `RAW_FRACTION` 类计数器拿不到 Base，算不出百分比。
- **磁盘温度按 `DeviceId` 配对**：WQL `ASSOCIATORS OF` 在 `ObjectId` 含引号时解析失败，改用整表查 + `DeviceId` 匹配。
- **实例名是机型相关的**：`RAPL_Package0_PKG`、`\_TZ.TZ01` 之类必须运行时探测，不能写死。
- **ADL（GPU 温度）实测失败并如实显示**：`atiadlxx.dll` 能加载、Overdrive5/6 温度接口都存在，
  但本机核显（Radeon 780M）取不到值 → 面板显示「未实现」，不做假数据。
- **PowerShell 取电池设计容量必须用投影查询**：`Get-CimInstance ... -ClassName BatteryStaticData`
  不带 `-Property` 会报「常规故障」，带 `-Property DesignedCapacity` 才返回值
  （等价于 C# 的 `SELECT DesignedCapacity FROM ...`）——这正是把「设计容量」误判成"需要管理员"的原因。
- **不引入内核驱动**：WinRing0 在微软易受攻击驱动黑名单里；CPU die 温度、主板/VRM/内存温度
  一律标注为「不支持」，需要时由用户自行运行 LibreHardwareMonitor。

### 说明

- exe 体积约 86 KB → 约 109 KB（新增传感器层）
- `build/build.ps1` 现在编译两个源文件（`OpenMIFS.cs` + `Sensors.cs`），BOM 检查覆盖全部源码
- 传感器**不需要管理员权限**（PDH / ACPI 热区 / WMI 普通用户可读）；
  只有磁盘温度（`MSFT_StorageReliabilityCounter`）需要，exe 本身已提权

## [0.2.1] - 2026-10-03

### 新增

- **诊断日志**：每次动作写一行到 `%LOCALAPPDATA%\OpenMIFS\openmifs.log`
  （exe / 图形脚本 / 命令行三种形态共用同一份；超过 1 MB 自动轮转）
  - 所有写操作都记录**写后读回值**，这是判断 EC 有没有理会写入的唯一依据
  - 异常记录异常类型、消息、堆栈首行与 InnerException
  - 首次创建写 UTF-8 BOM，Windows PowerShell 5.1 / 记事本不会读成乱码
- **开机自启**：注册计划任务 `OpenMIFS`（`/SC ONLOGON /RL HIGHEST`），登录时静默以管理员身份启动，
  不弹 UAC（注册表 `Run` 项做不到这一点，因为 exe 声明了 `requireAdministrator`）
- **OSD（Fn 屏幕提示）支持**
  - 状态行：服务与界面进程状态，异常时标红；同时显示系统 DPI 与 DPI 兼容标记状态
  - 「重启 OSD」：按 服务 → 界面进程 顺序重启（可逆，不改任何系统设置；
    等服务真正停稳再启动，避免「服务正在停止」导致启动失败）
  - 「诊断 OSD」：服务/进程/启动时间、安装目录、服务安装日志尾部、显示环境、系统 DPI、
    **`OSDEvents` 事件投递**与心跳折叠
  - **DPI 兼容修复（实验，可撤销）**：写 `HKLM\...\AppCompatFlags\Layers`
    的 `~ HIGHDPIAWARE` 标记（与「属性 → 兼容性 → 更改高 DPI 设置」同一条），
    取消勾选即删除；命令行 `osd dpi-on` / `osd dpi-off`
  - **自带屏幕提示**：每 1.5 秒轮询 Fn 键会改动的 EC 值（性能模式 / Fn 锁 / 触控板锁 / 键盘背光），
    外部变化就在屏幕下方弹提示 —— 官方 OSD 失效时的替代方案
  - 无界面模式 `OpenMIFS.exe --diagnose`，输出 `%LOCALAPPDATA%\OpenMIFS\osd-diagnose.txt`
  - 事件日志按**最新优先**读（`ReverseDirection`）并把心跳折叠成时间范围；
    正序读会被每 5 秒一条的心跳吃光扫描额度，从而误判"心跳早停了"
- **能力探测**：可写开关（风扇满速）在首次点击时做「写入 → 读回」实测，
  EC 忽略写入就判定为本机未实现，写入 `capabilities.txt` 缓存并**永久置灰**按钮；
  「重测功能」按钮可清空缓存重新探测
- 命令行新增 `osd status|restart|diagnose|dpi|dpi-on|dpi-off`、`startup status|on|off`、`log` 三个动作
- 托盘菜单新增「开机自启」「打开日志」「打开数据目录」
- 文档新增 [docs/OSD.md](docs/OSD.md)：厂商 OSD 的组件构成、证据怎么读、按风险排序的修复步骤
- `docs/screenshots/`：截图放进来即可在 README 显示

### 修复

- **顶部状态文字被下方分组框遮挡**：状态标签高度给足并整体重排界面（性能模式/风扇/硬件开关/
  键盘背光/启动与 OSD/状态面板/底部行）
- **未实现的功能按钮仍可点击**：现在一律 `Enabled = false` 并置灰；
  键盘背光不可用时按钮显示 `—`、分组框标题标注「未实现」（原先按钮文字被截断成「0（未」）
- 日志在每次追加时重复写 BOM 的问题（改为仅首次创建时写）
- PowerShell 版：`schtasks` / `sc` / `taskkill` 把错误写到 stderr，在
  `$ErrorActionPreference='Stop'` 下会被 PowerShell 5.1 当成终止性错误抛出
  （`startup status` 会直接崩）。新增 `Invoke-NativeQuiet` 统一调用
- PowerShell 版：DPI 上报误判。`powershell.exe` 是 DPI 不感知进程，
  `GetDpiForSystem` / `GetDpiForMonitor` / `Graphics.DpiX` 一律返回 96，
  会把 125% 缩放报成 100%。改为优先读
  `HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI`，再用「物理分辨率 ÷ 虚拟分辨率」兜底

### 说明

- exe 体积 50 KB → 约 86 KB（新增日志、OSD 模块、事件日志查询与 DPI 修复）
- 构建脚本新增 `System.Core.dll` 引用（`System.Diagnostics.Eventing.Reader`，用于读事件日志）
- PS1 图形界面在非 100% 缩放下可能略模糊（`powershell.exe` 不感知 DPI）；
  exe 版已声明 DPI 感知，更清晰

## [0.2.0] - 2026-10-03

### 新增

- **单文件 exe 版本**：`OpenMIFS.exe`（约 50 KB）
  - 常驻任务栏通知区域，可后台运行，托盘菜单直接切性能模式 / 风扇满速
  - 关闭窗口即最小化到托盘，双击托盘图标重新打开
  - 单实例保护（重复启动会提示，不重复占资源）
  - 清单内声明 `requireAdministrator`，双击即提权，无需手动右键
  - 只依赖 Windows 自带的 .NET Framework 4.8（Win10 1809+ / Win11 内置），
    不需要 .NET SDK、不需要 Visual Studio、不需要联网安装任何运行时
- **专用构建流程**
  - `build/build.ps1`：用系统自带 `csc.exe` 编译，产物 `dist/OpenMIFS.exe`，
    输出大小与 SHA256
  - `build/make-icon.ps1`：程序化生成多尺寸图标（16/24/32/48/64/128/256）
  - `.github/workflows/build.yml`：CI 脚本语法检查 → 构建 → 上传产物；
    打 `v*` tag 时自动发布 Release
- **原创应用图标**：圆角六边形 + 品牌紫→青渐变 + 三根递升档位柱

### 说明

- 图标刻意**不复用**机械革命的商标图形（紫色六边形 + 闪电 S）。
  那是对方的注册商标，放进公开 MIT 仓库有商标与著作权风险，也容易被误解为官方出品。
  想本地自用官方图标，可以 `build\build.ps1 -Icon <你的.ico>`，该文件不会入库。
- 构建脚本与 C# 源码**必须带 UTF-8 BOM**，否则 `csc` 会按系统代码页解析导致中文乱码；
  `build.ps1` 已内置自动补 BOM 的检查。

## [0.1.0] - 2026-10-03

首个版本。

### 新增

- 图形控制台 `src/mifs-gui.ps1`（纯 PowerShell + WinForms，单文件零依赖）
  - 性能模式切换（当前档位 `●` 标记）
  - 各风扇实时转速
  - Fn 锁 / 触控板锁定
  - 键盘背光亮度 0~3
  - 风扇满速开关
  - 状态面板与可调自动刷新
  - 未实现功能自动置灰标注
- 命令行工具 `src/mifs.ps1`
  - `status` / `probe` / `test` / `scan` / `bench` / `mode` / `fanboost` / `kbd` / `raw`
  - `scan` 用于发现驱动清单外的功能号
  - `bench` 用实测数据校正性能模式映射
- 双击启动器 `mifs.cmd`（命令行）与 `mifs-gui.cmd`（图形界面），自动提权并绕过执行策略

### 说明

- 性能模式映射按无界 14 Pro 2023 实测校正为 `0=性能 1=均衡 2=低功耗`
  （上游 Linux 驱动标注为 `0=均衡 1=性能 2=低功耗`，两者相反）
- 已确认 MIFS 接口**不提供**电池充电阈值，也无法调节风扇转速