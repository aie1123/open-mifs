# AGENTS.md — OpenMIFS 项目约定

> 给 AI 编码助手（以及新加入的人）看的。**动手前先读完这页**，这里写的都是踩过坑换来的。

---

## 1. 这个项目是什么

不给"官方没出控制中心"的同方（TongFang）/机械革命笔记本，用**零依赖单文件 exe** 调 BIOS 暴露的
**MIFS（MiInterface）ACPI WMI** 接口：性能模式、Fn 锁、触控板锁、键盘背光、风扇转速等，
外加一套**免驱动**传感器面板（AMD 通道 + PDH + WMI）。

产物只有三种形态，**共用同一套接口与同一份日志**：

| 形态 | 文件 | 说明 |
| :--- | :--- | :--- |
| 托盘 exe ⭐ | `src/csharp/OpenMIFS.cs` + `src/csharp/Sensors.cs` | 发布物就是这个 |
| 图形脚本 | `src/mifs-gui.ps1` | PowerShell + WinForms |
| 命令行 | `src/mifs.ps1` | `mifs.cmd` 启动 |

---

## 2. 三条硬约束（违反其一即视为错误实现）

1. **不加载任何内核驱动、不打包任何第三方二进制。**
   WinRing0 在微软"易受攻击驱动黑名单"里；PawnIO 之类是第三方内核组件。
   `atiadlxx.dll`（AMD 显卡驱动自带）可以调用 —— 它属于**用户态**系统组件，不是我们引入的驱动。
2. **不造假数据。** 拿不到就显示「未实现 / 不支持」，并把**原因**写进提示与日志。
   绝不用常数、估算值或"看起来差不多"的值冒充实测。
3. **不声称未验证的结论。** 说过"已修复/已生效"就必须给出可复现证据（日志行、截图、命令输出）。
   验证不了就明说"未验证，需要你在提权/真机上确认"。

---

## 3. 验证纪律（本项目的核心方法）

**判断"写入是否真的生效"，必须找一个独立于寄存器镜像的物理量。**

| 场景 | 错误判据（踩过） | 正确判据 |
| :--- | :--- | :--- |
| 风扇满速 | `fn=20` 寄存器读回值变化 | **风扇转速**（`fn=13` 的 RPM 变化） |
| 性能模式 | 标签文字 | `bench` 实测的 CPU 性能百分比 + 风扇峰值 |
| 磁盘温度 | — | 与 CrystalDiskInfo 对照（它读到 35 ℃ 就说明平台暴露） |
| 传感器是真是假 | 单次读数 | **加载前后同向变化**（如 8 线程负载下三个温度同时上升） |

推论：
- 改动"写入类"功能后，必须跑一次实测并**把数值贴进提交说明**。
- 只做语法检查不算验证。CLI 动作必须**真跑一遍**（`fan` 动作曾因漏加 `ValidateSet` 直接不可用）。
- 枚举值、结构体偏移、协议常量**必须查官方头文件/文档**，不能按"第几个字段就是第几个值"推算
  （`STORAGE_PROPERTY_ID` 中间跳到 48，曾因此四个属性 ID 全部偏低 30，白查 4 轮）。

---

## 4. 工具链与构建

| 项 | 值 |
| :--- | :--- |
| 编译器 | 系统自带 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（.NET Framework 4.8） |
| **语言级别** | **C# 5** —— 不能用字符串插值 `$"…"`、不能用 `?.`、不能用 `nameof`、不能用表达式体成员 |
| 构建 | `powershell -File build\build.ps1` → 输出 `dist\OpenMIFS.exe`，末尾打印 `OPENMIFS_BUILD_OK path=… size=… sha256=…` |
| 引用 | `System.dll` `System.Core.dll` `System.Drawing.dll` `System.Windows.Forms.dll` `System.Management.dll` |
| 源码编码 | **必须 UTF-8 with BOM**（`build.ps1` 会给 `.cs` 自动补；`.ps1` 手写时必须自己加，否则
  Windows PowerShell 5.1 会按 GBK 解析出乱码/语法错误） |
| 构建确定性 | 不支持 `/deterministic`：**同源码两次构建字节不同**（大小稳定、哈希不稳定）。所以"大小对得上 CI"可作证据，"哈希对不上"是正常的 |

**新增源文件时**：把它加进 `build\build.ps1` 的 `$sources` 数组，否则 CI 会编译失败。

---

## 5. 验证副本的固定套路（不改用户环境）

本机 exe 声明了 `requireAdministrator`，直接用当前（非提权）会话测不了。
标准做法是**造一个 asInvoker 的临时副本**：

1. 复制 `OpenMIFS.cs` / `Sensors.cs` 到 `%TEMP%\<name>\`
2. 把数据目录改名避免污染真实配置：`Path.Combine(b, "OpenMIFS")` → `Path.Combine(b, "OpenMIFS_<tag>")`
3. 把互斥体改名：`Global\OpenMIFS_SingleInstance` → `Global\OpenMIFS_SingleInstance_<tag>`
4. `app.manifest` 里 `requireAdministrator` → `asInvoker`
5. `csc` 编译到临时目录，然后 `--sensors` / `--diagnose` / 直接启动截图
6. **收尾必须删除临时目录与 `%LOCALAPPDATA%\OpenMIFS_<tag>`**

截图套路：`GetWindowRect` + `CopyFromScreen`；先 `SetProcessDPIAware()`。
窗口可能开在虚拟显示器上 → 抓不到就用 `MoveWindow` 挪到固定坐标（提权窗口挪不动，受 UIPI 限制）。

---

## 6. 发布流程

1. 改代码 → `build\build.ps1` → 用第 5 节的副本跑一遍验证
2. 更新 `CHANGELOG.md`（**写清"验证"小节**：跑了什么、看到什么数字）
3. 提交 + `git tag -a vX.Y.Z` + `git push origin main` + `git push origin vX.Y.Z`
4. CI（`.github/workflows/build.yml`）会做：语法检查 → CLI 动作一致性检查 → UTF-8 BOM 检查 →
   图标存在 → 构建 → 上传 artifact → tag 时建 Release
5. **验证发布产物**：下载 Release 里的 exe，检查大小与关键字符串（`Has $bytes '…'` 搜 UTF-16 字面量）。
   注意本机直连 GitHub 不通，需走代理 `http://127.0.0.1:7890`（Clash 未启动时会失败，等它起来再推）

版本号在 `src/csharp/OpenMIFS.cs` 的 `AssemblyVersion` / `AssemblyFileVersion`，
界面与托盘显示的是 `MifsApp.VersionText`（三位，如 `0.5.3`）。

---

## 7. 代码地图（改哪里）

| 想改的东西 | 位置 |
| :--- | :--- |
| MIFS 调用、功能号、模式映射 | `OpenMIFS.cs` 的 `Mifs` / `ModeMap` |
| 界面与键值行渲染 | `MainForm`（`KvRow` / `RenderKeyValues` / `SensorOrder`） |
| 托盘菜单、托盘提示、托盘图标 | `TrayContext` / `TrayText` / `TrayIcon` |
| 传感器读取 | `Sensors.cs`（`ReadAll` / `Render` / `AdlPmlog` / IOCTL 兜底） |
| OSD 诊断与自带屏幕提示 | `OpenMIFS.cs` 的 `Osd` / `OsdDpi` / `OsdOverlay` |
| 开机自启（计划任务） | `OpenMIFS.cs` 的 `Startup`（**必须带 `--tray`**） |
| 设置项（`settings.txt`） | `Settings.Get/Set`；现有键：`osd_hint`、`tray_items`、`tray_icon`、`tray_icon_t/p/l` |
| 日志 | `Log`（`%LOCALAPPDATA%\OpenMIFS\openmifs.log`，>1 MB 轮转） |

线程模型：UI 线程做界面；传感器读取在线程池（首次 1~2 秒，PDH 需预热 700 ms）；
**窗口隐藏时 `RefreshSensors()` 会直接返回** —— 托盘需要数据时走 `RequestHiddenSensorSnapshot()`。

---

## 8. 交流与输出风格

本仓库作者习惯**极简、可执行**的输出：

- 先给动作/结论，再给理由；不要开场白、不要"希望有帮助"这类收尾
- 多步任务编号；一次不超过 5 条
- 报错给"原因 + 修法"，不给"糟糕，好像出问题了"
- 时间估计给具体数字（"约 1.5 小时"），不给"需要一些时间"
- 改动完成要**让成果可见**：贴数字、贴日志行、贴截图路径

---

## 9. 禁区清单

- ❌ 打包/加载内核驱动（WinRing0、PawnIO、自家驱动）
- ❌ 为了"功能看起来全"而伪造或兜底成常数
- ❌ 未经实测就给功能打 ✅（例：风扇满速必须先看 RPM 变化）
- ❌ 修改用户环境：真实 `settings.txt`、真实计划任务、系统 DPI 兼容标记，除非用户明确要求
- ❌ 在没有证据时推断"平台不支持"（磁盘温度那轮就是反例：CrystalDiskInfo 能读，说明是我们写错了）
