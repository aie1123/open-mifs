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
| 托盘 exe ⭐ | `src/csharp/OpenMIFS.cs` + `src/csharp/Sensors.cs` + `src/csharp/Ui.cs` | 发布物就是这个 |
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

截图套路：`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9)` 取**可见**矩形 + `CopyFromScreen`；先 `SetProcessDPIAware()`。
**不要用 `GetWindowRect`**：Win10/11 会多出约 8px 不可见边框，截出来的左/下边缘会带上"背后窗口"的内容（曾误判成 UI 文字溢出）。
截不到就把窗口 `SetWindowPos(HWND_TOPMOST)` 顶到最前（`SetForegroundWindow` 常被前台锁定策略挡掉）。
窗口可能开在虚拟显示器上 → 抓不到就用 `MoveWindow` 挪到固定坐标（提权窗口挪不动，受 UIPI 限制）。

**回归模式**：`o.exe --toggle-test=3` 会"隐藏启动 → 置为最小化+已隐藏 → 反复切换可见性"，
用于守住历史上那条栈溢出崩溃路径（见 CHANGELOG 0.5.4）。改可见性相关代码后跑它。

**UI 可见性切换的铁律**（0.5.4 + 0.5.5 两次血案）：

1. **防重入**：`HideToTray()` / `Restore()` 会改 `ShowInTaskbar` → **触发句柄重建 → 再次引发 `Resize`**，
   必须用互斥标志挡住（否则 0.5.4 那种栈溢出：进程静默消失，只能靠事件日志 `0xc00000fd` 认出）。
2. **`HideToTray()` 必须把 `WindowState` 归位**，否则 `Resize` 里 "== Minimized" 判定恒真 → 无限互递归。
3. **`Restore()` 必须"先 `Show()`，再 `WindowState = Normal`"**（0.5.5 血案：反过来写会把窗口
   冻结在最小化占位坐标 `(-25600,-25600)`，`Show()` 只让它"可见"、不会挪回屏幕 → 用户点显示却什么都看不到）。
4. **显示后必须做屏幕内兜底**（`EnsureOnScreen()`）：矩形若完全落在所有屏幕之外就挪回主屏居中。
   没有这一步，坐标一旦跑到屏幕外，用户再也点不回来，只能重启程序。
5. `--tray` 启动路径**不要用 `WindowState = Minimized`**（占位坐标的来源），`Opacity = 0` 已足够避免闪烁。

回归模式 `--toggle-test=N` 每次切换后会记录 `Visible/State/Bounds/在屏幕内`，改可见性代码后必跑。

---

## 6. 发布流程

1. 改代码 → `build\build.ps1` → 用第 5 节的副本跑一遍验证
2. 更新 `CHANGELOG.md`（**写清"验证"小节**：跑了什么、看到什么数字）
3. 提交 + `git tag -a vX.Y.Z` + `git push origin main` + `git push origin vX.Y.Z`
4. CI（`.github/workflows/build.yml`）会做：语法检查 → CLI 动作一致性检查 → UTF-8 BOM 检查 →
   图标存在 → 构建 → 上传 artifact → tag 时建 Release
5. **验证发布产物**：下载 Release 里的 exe，检查大小与关键字符串（`Has $bytes '…'` 搜 UTF-16 字面量）。
   注意本机直连 GitHub 不通，需走代理 `http://127.0.0.1:7890`（Clash 未启动时会失败，等它起来再推）

> ⛔ **注意**：上面 1~5 是**旧流程**，已作废（只有第 5 条"验证发布产物"仍然有效，
> 移到了 §6.1 第 7 步）。作者明确要求改成"两段式"，见 §6.1。
> 未经作者本地验收就 push / 建 Release 属于流程错误（作者原话：*"能不能每次改完先别提交发布，
> 我在本地替换测试没问题后再推送和发布？"*）。

### 6.1 两段式流程（**当前生效**）

**第一段：改 + 构建 + 交给作者测试 —— 此段禁止 `git commit` / `git tag` / `git push` / 建 Release**

1. 改代码 → `build\build.ps1` → 产出 `dist\OpenMIFS.exe`
2. 自己做机器自查（第 5 节的 asInvoker 副本截图 / `--toggle-test` / 真跑 CLI 动作）—— 这是"我能证明它能跑"，
   **不等于**作者验收
3. 向作者汇报三件事，然后**停下等回复**：
   - 改了什么（逐条对应他提的问题）
   - `dist\OpenMIFS.exe` 的**大小 + SHA256**（构建脚本末尾会打印）
   - 覆盖哪个路径去测（本机是 `C:\Users\16609\OpenMIFS.exe`，先退掉托盘里的旧实例）
4. 版本号、`CHANGELOG.md` 可以**先写好但不提交**，发布时一次成型
5. 作者说"有问题" → 在**同一批未提交改动**上继续改，回到第 1 步（不要为了干净而中途提交）

**第二段：作者明确说"没问题 / 推送 / 发版"之后**

6. 确认版本号 + 提交 + `git tag -a vX.Y.Z` + `git push origin main` + `git push origin vX.Y.Z`
7. 验证发布产物（大小、UTF-16 关键字符串），确认 CI badge 通过

版本号在 `src/csharp/OpenMIFS.cs` 的 `AssemblyVersion` / `AssemblyFileVersion`，
界面与托盘显示的是 `MifsApp.VersionText`（三位，如 `0.5.3`）。

---

## 6.2 大段代码改动的铁律（血的教训，v0.6.3 期间两次误删方法）

自动化/AI 改这个仓库的大段代码时（尤其**整块布局方法**），**禁止"按括号切片"替换**：

```powershell
# ❌ 禁止：靠"方法头 → 第一个 8 空格右括号"定位
$i = $t.IndexOf('        private void LayoutPrefs(double s)')
$j = $t.IndexOf("`n        }`n", $i)          # ← 这个 } 未必是本方法的结尾
$t = $t.Substring(0, $i) + $new + $t.Substring($j)
```

原因：仓库里方法之间只隔一个空行，方法数又多（`MainForm` 60+ 个），一旦定位到下一个方法的右括号，
就会把**中间的方法整段删掉**。实测后果：

| 事故 | 被删掉的方法 | 症状 |
| :--- | :--- | :--- |
| v0.6.3 第 1 次 | `TogglePrefs`（偏好设置展开/收起） | `CS0103 当前上下文中不存在名称 TogglePrefs` |
| v0.6.3 第 2 次 | `OnElevateClick`（以管理员重启） | `CS0103 当前上下文中不存在名称 OnElevateClick` |

**正确做法（三选一）**

1. **整方法精确替换**：把旧方法全文作为 `old_string`，新方法全文作为 `new_string`（最稳，推荐）
2. **唯一后继标记切片**：结束锚点用**紧随其后、并且只出现一次**的文本（如下一个方法的 XML 注释首行），
   不要用 `}` 这种到处都有的字符
3. 小改动一律**逐行精确替换**（单行/固定多行文本），不要碰结构

**改完必须自检（两条，缺一不可）**

```powershell
# ① 方法存活检查：改动区域涉及的方法名逐个计数，定义+调用点数量应保持不变
foreach ($m in 'TogglePrefs','OnElevateClick','LayoutPrefs') {
  "$m = " + (Select-String -Path $f -Pattern ([regex]::Escape($m)) | Measure-Object).Count
}
# ② 括号配对统计：两个数必须相等
$t = [System.IO.File]::ReadAllText($f,[System.Text.Encoding]::UTF8)
"{{ = " + ([regex]::Matches($t,'\{{')).Count + "  }} = " + ([regex]::Matches($t,'\}}')).Count
```

另外两条同类教训（.NET Framework 自带 csc 的限制）：

- **C# 5 不支持局部函数**：`double f(double v) { ... }` 写在方法体里会报 `CS1513 应输入 }`，
  必须改成内联表达式或私有静态方法
- **PowerShell 双引号串里的 `""` 会变成一个 `"`**：生成 C# 代码时，含 `""` 的行要用**单引号串**书写，
  否则会产出 `_x.Text = ";` 这种未闭合字符串（报 `CS1010 常量中有换行符`）

---
## 7. 代码地图（改哪里）

| 想改的东西 | 位置 |
| :--- | :--- |
| MIFS 调用、功能号、模式映射 | `OpenMIFS.cs` 的 `Mifs` / `ModeMap` |
| 界面 token（颜色/字体/间距，含档位配色 `color_l1..l4`）与自绘控件 | `src/csharp/Ui.cs`（`Ui` / `FlatButton` / `RecessedPanel` / `SectionTitle` / `ReadoutRow` / `BigReadout`） |
| 界面布局与读数渲染 | `MainForm`（`BuildUi` / `BuildPrefsUi` / `RefreshAll` / `RefreshSensors` / `ApplyBig` / `LevelOf`）—— 设计依据见 `docs/UI-DESIGN.md` |
| 托盘菜单、托盘提示、托盘图标 | `TrayContext` / `TrayText` / `TrayIcon` |
| 传感器读取 | `Sensors.cs`（`ReadAll` / `Render` / `AdlPmlog` / IOCTL 兜底） |
| OSD 诊断与自带屏幕提示 | `OpenMIFS.cs` 的 `Osd` / `OsdOverlay`（`OsdDpi` 已于 v0.6.2 移除，需要时手动写注册表，见 `docs/OSD.md`） |
| 开机自启（计划任务） | `OpenMIFS.cs` 的 `Startup`（**必须带 `--tray`**） |
| 设置项（`settings.txt`） | `Settings.Get/Set`；现有键：`osd_hint`、`tray_items`、`tray_icon`、`tray_icon_t/p/l/gt`、`auto_refresh`、`color_l1..color_l4`、`color_raw` |
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
