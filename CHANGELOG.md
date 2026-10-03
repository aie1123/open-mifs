# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

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