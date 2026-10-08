# Changelog

本项目遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [0.3.7] - 2026-10-03

### 文档

- **风扇调速在本机定论：不可用（有实测证据）** —— `mifs.cmd fan test` 实测
  基线 3143 RPM → 开满速 8 秒后 3166 RPM（**+23，噪声内**），寄存器读回始终 0。
  原因是电源类型门控（满速/性能模式要求圆口 DC），本机 `fn=19` 恒为 Type-C 且只有 USB-C 供电口
  - [docs/FAN-CONTROL.md](docs/FAN-CONTROL.md) 补上实测结论、剩余选项（含各自的真实风险）与"为什么这次能下结论"
  - [docs/TESTED-MODELS.md](docs/TESTED-MODELS.md) 记录实测数据
  - README「已知限制」第 2 条改为最终结论 + **推荐替代方案**：切低功耗/均衡模式降功耗墙 → EC 自己降转速
- 记下一条可复用的判据规矩：凡"写入是否真的生效"，都要找一个**独立于寄存器镜像的物理量**
  （风扇看转速、性能模式看实测性能百分比、充电看实际注入电量）—— 这次误判就是只看了寄存器镜像

## [0.6.0] - 2026-10-08

### 界面整体重构：从"7 个同款分组框"到「仪表台」

依据 `docs/UI-DESIGN.md`（v2，用 `frontend-design` + `ui-ux-pro-max` 两套技能做的定调）**完整重做界面**。
新增 `src/csharp/Ui.cs`：设计 token 的唯一来源（颜色 / 字体 / 间距 / 表面语义）+ 自绘控件。

**结构改动**

| 改动 | 说明 |
| :--- | :--- |
| **取消「状态」镜像面板** | 原来右列 10 行里 7 行是左列控件的回读（性能模式/风扇1/风扇2/风扇满速/Fn 锁/触控板锁/键盘背光）。现在回读贴在控件旁，环境信息进读数区底行 |
| **风扇转速只读一次** | 原来同一数据在三处显示、两条独立读取路径（左列/状态面板 `3075/3194` vs 传感器面板 `3049/3067`，数值互相打架）。现在全场一次读取，只在关键读数区显示 |
| **新增「关键三项」** | 温度 / 功耗 / 风扇 三个 17pt 等宽大数字，三列等宽 —— 承载"打开 3 秒内回答：什么模式、烫不烫、要不要切" |
| **读数区三簇** | 热与功耗 / 频率与负载 / 存储与电池，簇间细线；数值 11pt 等宽**右对齐**、单位左对齐固定列 → 小数点成列 |
| **控制区去外框** | 7 个 GroupBox → 区块标题 + 细线分区 + 24px 节奏 |
| **偏好设置折叠** | 「托盘提示」从首屏 252px 的主区块收进 `▸ 偏好设置`（默认收起） |

**表面语义（本次设计的核心决定）**：凹陷方角白底 = 只读读数；平面圆角 4 = 可交互。
取代"所有区块都长一样"的做法，不需要靠标题文字判断哪里能点。

**可访问性与排版修复**

- **不再只靠颜色**：温度同时给档位字（`56 ℃ 温度 · 温` / `51 ℃ 凉`），依据 `ui-ux-pro-max` 的 `Color Only` 规则（严重度 High）
- **键盘可达**：自绘按钮带 2px 焦点环 + 空格/回车触发；全部可交互控件补 `TabIndex` 与 `AccessibleName`
- **配色 token 化**：23 处硬编码 `Color.FromArgb` 收敛到 `Ui` 常量表（标签色 `#6B6B73` 实测对比度 5.28:1）
- **修掉 4 处文字截断**：`OpenMIFS`、`屏幕提示`、`风扇满速（Type-C 供电下被…`、传感器底栏 `只读（PDH +…`
- **去掉 4 处 `·` meta 串与版本号重复**（标题栏已有版本，状态条不再重复）
- **`未实现` 不再当数值渲染**：不可用项显示 `—`，原因进 ToolTip
- **`32.0 GB` → `32 GB`**（去掉多余 `.0`）；GPU 利用率长串改为只显示最忙引擎，合计进 ToolTip
- 读数行高 22 → 24（8pt/11pt 混排下 ratio ≈1.5）

**验证**

- 构建：`OPENMIFS_BUILD_OK size=158720`（新增 `Ui.cs` 已加入 `build.ps1` 的 `$sources`）
- 非提权副本截图：`docs/screenshots/gui-v0.6.0.png`（942×868）—— 三区布局、关键三项大数字、
  三簇明细、右对齐数值列、档位字、折叠的偏好设置全部就位；无文字截断
- 可见性回归：`ui.exe --toggle-test=3` 退出码 **0**，日志 `回归：全部完成，未崩溃`，
  三次切换 `Bounds={X=961,Y=439,958x862} 在屏幕内=True`（0.5.4/0.5.5 那条栈溢出路径未复发）
- 提权态未截（UIPI 挡 MoveWindow）：提权下 16/16 项可用的完整效果需在真机实例上确认

**已知未做**（下一轮）：未实现项的展开式折叠（现在只给数量与清单文本）、
`偏好设置` 里的数据源探测按钮位置仍偏下。
## [0.5.6] - 2026-10-08

### 改进：托盘提示设置区的排版与逻辑重做

**阈值行改为"跟随图标数据源"**（选 CPU 温度就只给温度阈值，不再是三套并排/逗号分隔）：

- 三个**带上下箭头的数字框**（`NumericUpDown`）代替原来的文本框：
  `图标显示 [CPU 温度 ▾]　变色阈值 [55▲▼] [70▲▼] [85▲▼]`
- 每档范围按指标给定：温度 20~120 ℃、功耗 1~150 W、负载 1~100 %，步进 1
- **自动维持严格递增**：改低档把中档顶上去、改中档把高档顶上去（不会弹"顺序错误"）
- 底部说明动态生成：`温度阈值（℃）：≤55 绿 · ≤70 琥珀 · ≤85 橙 · 更高红`
- 图标显示选「无」→ 阈值行整行隐藏（语义自洽）
- 数字框**右键**「恢复默认阈值」（只复位该指标，不占空间）

**排版/文案修复**（截图里能看到的丑）：

| 问题 | 原因 | 修复 |
| :--- | :--- | :--- |
| 「图标显」「变色阈」被切 | Label 58 px 装不下 4 个中文字 | 显式 `AutoSize=false` + 72 px |
| 「温」「功」「负」被切 | 单元标签 30 px | 改为单一动态标签，宽度足够 |
| 底部提示末尾被切 | 432 px 装不下 38 字 | 文案缩短为动态阈值说明 |
| 勾选列表出滚动条 | 116 px 装 8 项 | 高度 172 px（8×20 正好） |
| 勾选项带「（≤8）」行话 | 实现细节外露 | 去掉，改由列表 ToolTip 统一说明 |
| 预览区留白 + 右侧控件拥挤 | 左右高度不齐 | 预览 228×100、字数行显示 `6/62 字符` |
| 「恢复默认」会把阈值一起重置 | 一个按钮管两件事 | 只复位显示项；阈值走右键菜单 |

生效即保存（无"保存"按钮），改动立刻重画托盘图标。

## [0.5.5] - 2026-10-08

### 修复：点「显示主界面」看不到窗口（窗口被冻结在屏幕外）

**现象**：托盘的「显示主界面」和双击图标都"没反应"，窗口不出现。

**实测证据**（用户正在运行的 0.5.4 实例，PID 14484）：

```
主窗口  visible=True  iconic=False  rect=(-25600,-25600)-(-25040,-25152)
屏幕范围 0,0 2304x1440     → 在屏幕内=False
```

`(-25600,-25600)` 是 **Windows 给最小化窗口的占位坐标**。窗口其实是"可见"的，只是被摆在桌面之外。

**根因（v0.5.4 引入的回归）**：0.5.4 为修栈溢出，把 `WindowState = Normal` 挪到了 `Show()`
**之前**：

```csharp
// 0.5.4（错）
if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;  // 冻结占位坐标
ShowInTaskbar = true;
Show();      // 只让它"可见"，不会挪回屏幕
```

配合 0.4.4 的 `--tray` 启动路径（`WindowState = Minimized` + `Opacity = 0` + `Show()` + `HideToTray()`），
窗体从一开始就带着占位坐标，之后再也回不到屏幕内。

**修复**：

1. `RestoreCore()` 改为 **先 `Show()`，再 `WindowState = Normal`**（WinForms 会走 `SW_RESTORE` 正确恢复 bounds）
2. 新增 `EnsureOnScreen()`：矩形若完全落在所有屏幕之外 → 挪回主屏居中并写警告日志（保命兜底）
3. `--tray` 启动路径**去掉 `WindowState = Minimized`**（占位坐标的来源；`Opacity = 0` 已足够避免闪烁）
4. 回归模式每次切换后记录 `Visible/State/Bounds/在屏幕内`，作为可复核证据

### 验证

| 阶段 | 结果 |
| :--- | :--- |
| 修复前（用户运行中的 0.5.4） | `rect=(-25600,-25600)`、**在屏幕内=False** |
| 修复后（`--toggle-test=12`，同一台机） | 12 次切换（显/隐各半）日志全部 `Bounds={X=961,Y=439,958x862} 在屏幕内=True`，未触发位置兜底 |

> 说明：0.5.4 的防重入互斥仍然保留（那是修栈溢出必需的），本次只是把**顺序**改回来并加上屏幕内兜底。

## [0.5.4] - 2026-10-08

### 修复：从托盘点「显示主界面」导致栈溢出崩溃（0xC00000FD）

**现象**：窗口先被最小化、再收进托盘后，从托盘菜单点「显示主界面」→ 进程瞬间消失
（无异常日志、无退出记录）。Windows 事件日志：

```
出错应用程序名称：OpenMIFS.exe，版本 0.5.3.0
出错模块名称：System.Windows.Forms.ni.dll
异常代码：0xc00000fd            ← STATUS_STACK_OVERFLOW
```

**根因（互递归）**：

```csharp
Resize += delegate { if (WindowState == FormWindowState.Minimized) HideToTray(); };
```

`HideToTray()` 会改 `ShowInTaskbar` → 触发窗口句柄重建/重新布局 → **再次引发 `Resize`**；
而它**从不重置 `WindowState`**，于是判定条件永远为真 → `Resize → HideToTray → Resize → …`
无限互递归。`Restore()` 里 `ShowInTaskbar = true` 同样会引发这一串。

**修复**（三处）：

1. 新增 `_visibilityBusy` 互斥：`HideToTray()` / `Restore()` 进入即置位，`Resize` 处理器见到置位直接返回
2. `HideToTray()` 结束后把 `WindowState` **归位为 Normal**（让判定条件不再恒真）
3. `Restore()` **先归位 WindowState，再做**会引发句柄重建的操作（顺序反了同样会重入）

### 新增：可见性切换回归模式

`OpenMIFS.exe --toggle-test=3`：隐藏启动 → 置为"最小化 + 已隐藏" → 反复切换可见性并记录，
用于回归这条历史崩溃路径（N 默认 3，可用 `--toggle-test=N` 指定）。

### 验证

| 阶段 | 结果 |
| :--- | :--- |
| 修复前复现 | 进程退出码 **-1073741571 = 0xC00000FD**（与用户崩溃一致），第一次切换即死，事件日志新增 Application Error |
| 修复后回归 3 次 | 退出码 **0**，日志出现「全部完成，未崩溃」，事件日志**无新增**崩溃记录 |

## [0.5.3] - 2026-10-04

### 新增：托盘图标变色阈值可配置

- 界面「变色阈值」一行三个输入框（温度 / 功耗 / 负载），每个填**三个升序数值、逗号分隔**
  （如 `55,70,85` = 绿/琥珀/橙/红四档分界）；存 `settings.txt` 的
  `tray_icon_t` / `tray_icon_p` / `tray_icon_l`
- 失焦即校验：必须 3 个正数且严格递增，否则**不保存**、输入框回滚并提示原因
- 配置文件值不合法（个数不对/非数字/不递增）→ **回落默认并写回 settings.txt**（自愈）
- 改完立即重画图标；「恢复默认」一并复位阈值

### 修复

- **图标尺寸不能只信一个来源**：本机实测 `GetDpiForSystem()` 返回 96（注册表 `AppliedDPI=120` 即 125%），
  只按它会画成 16px 交给 20px 的托盘 → 被系统放大而发糊。
  改为 `max(16×dpi/96, SM_CXSMICON)` 再兜下限 20 —— 画大被缩小只是轻微发软，画小被放大明显糊，宁大勿小
- 档位变化也触发重画并写日志（便于确认阈值改动真的生效）

### 验证

| 场景 | 结果 |
| :--- | :--- |
| 默认阈值 `55,70,85`，实测 54 ℃ | 档位 1（绿）✓ |
| 改为 `30,35,40`，实测 55 ℃ | 档位 4（红）✓ |
| 写入非法值 `80,70,60` | 回落默认并**写回** settings.txt（未再告警）✓ |
| 图标尺寸 | 20px（本机托盘实际尺寸）✓ |

## [0.5.2] - 2026-10-04

### 修复：顶部"本机未实现"那行的误导

**问题**：顶部写着「本机未实现：MIFS CPU 温度、MIFS CPU 功率」，可右侧传感器区明明显示
`CPU 温度 50 ℃`、`CPU 功耗 16.49 W` —— 因为那两项指的都是 **MIFS 的功能号 22/23**（恒为 0），
而面板上的 CPU 温度/功耗走的是 **AMD 通道（ADL PMLog）+ PDH**。列出来只会让人误读成
"这台机器读不到 CPU 温度"。

**改动**：
- `MIFS CPU 温度 / MIFS CPU 功率` **不再计入顶部清单**（改为启动后探测一次、写一条日志备查）
- 顶部第二行**只在真有缺失时出现**，措辞改为「本机不支持：X、Y（其余功能正常）」并限长 56 字符；
  没有缺失就只剩一行，不再显示"无，全部功能可用"这种噪音
- 效果：本机（缺失清单原本只有那两项）改完后**整行消失**

## [0.5.1] - 2026-10-04

### 新增：把数值画进托盘图标

- 下拉可选 **无（程序图标）/ CPU 温度（默认）/ CPU 功耗 / CPU 负载**：
  圆角色块 + 白字，颜色按档位（温度 `<55` 绿 → `<70` 琥珀 → `<85` 橙 → 其余红）
- 尺寸按系统小图标度量（`GetSystemMetrics(SM_CXSMICON)`）：100%→16px、125%→20px、150%→24px；
  2 字符用大字号、3 字符自动缩一档；`SingleBitPerPixelGridFit` 保证小字号不糊
- **只提供短数值**：16×16 的极限是 2~3 字符，风扇 RPM（4 位数）不适合，故不提供
- 值变了才重画；配置存 `settings.txt` 的 `tray_icon`，切换立即生效
- 新增 `OpenMIFS.exe --icon-preview`：输出 `tray-icon-preview.png`（16/20/24px × 4 数值放大对照图）

### 修复

- **GDI 句柄泄漏**：`Icon.FromHandle` 不接管所有权，必须 `DestroyIcon` 且 Dispose 旧 Icon/Bitmap
  （否则每 5 秒泄漏一个 GDI 对象）
- **隐藏态图标取不到值**：`TrayText.SensorTime` 改为**单独计时**（原来用总快照时间，
  被状态快照刷新误重置，导致 8 秒节流永不触发）；节流判断同时考虑"提示项**或图标**需要传感器"
- 无值时**保持程序图标**（不画空白色块），数据恢复后自动换回数字
- **内存规格补单位**：`DDR5-5600` → `DDR5-5600 MT/s`，并在提示里给出换算
  `5600 MT/s（≈2800 MHz 实际时钟）` —— 原来只有数字容易被误读成 MHz
- 文档：右键菜单详细摘要按用户决定**标记为不做**；新增托盘图标章节（含三个坑的记录与实测）

## [0.5.0] - 2026-10-04

### 新增：托盘悬停提示可自定义（白名单 + 最坏宽度预算）

**方案**：不做自由模板，**只允许一组"最坏情况下也放得下"的项目**，从源头保证不溢出
（悬停提示是 Win32 单行字符串，.NET `NotifyIcon.Text` 上限 63 字符，超长会被静默截断成
`风扇 2495/25…` 这种半个数字）。

- 8 个项目各登记**最坏宽度**：版本 8 / 性能模式 3 / 风扇转速 17 / CPU 温度 7 / CPU 功耗 9 /
  CPU 频率 9 / CPU 负载 7 / 电池电量 7（分隔符固定 `" · "`，顺序固定）
- **预算门控**：`WorstTotal = Σ最坏宽度 + (n-1)×3 ≤ 62`；超预算时**拒绝勾选**并说明原因，
  界面实时显示 `最坏情况 34/62 字符　实际 6 字符`
- 没有值的项（未实现/不支持/需要管理员）**静默省略**，不会把"未实现"塞进提示
- 值按最坏宽度上限截断；`Build()` 另有一次"**整项**丢弃"兜底，绝不按字符切
- 主窗体新增分组「托盘悬停提示（按最坏宽度限额）」：勾选 + 预览 + 恢复默认；配置存
  `settings.txt` 的 `tray_items`，改动立即生效；配置损坏或组合超限自动回落默认

### 修复

- **窗口收进托盘后传感器不再刷新**（`RefreshSensors()` 首行 `if (!Visible) return;`）——
  勾选 CPU 类项目时托盘会拿到空值。新增 `MainForm.RequestHiddenSensorSnapshot()`：
  窗口隐藏且快照超过 8 秒时，由托盘触发一次"只采快照、不碰控件"的后台采集

### 文档

- [docs/TRAY-TOOLTIP.md](docs/TRAY-TOOLTIP.md) 改写为最终方案（含最坏宽度表、预算门控、
  实测验收、以及被否决的"自由模板 + 优先级丢弃"方案）

## [0.4.4] - 2026-10-04

### 修复

- **开机自启不再弹主界面**：计划任务的命令行加上 `--tray`，登录时只驻留通知区域。
  - exe 新增 `--tray`（亦接受 `-t` / `/tray` / `--silent` / `--minimized`）：
    只创建窗口句柄（传感器刷新依赖句柄）但立即收进托盘，**不显示主界面**，也不抢焦点
  - `--tray` 模式下若已有实例在运行，**静默退出**（不再弹"已在运行"对话框，
    避免开机自启与手动启动撞车时打扰）
  - `Startup.Set()` 创建任务时写入 `--tray`；启动时 `Startup.RepairIfNeeded()`
    用 `schtasks /Query /XML`（元素名与系统语言无关）检查老任务，
    **不带 `--tray` 就自动重建** —— 老版本用户无需手动处理
  - `mifs.ps1 startup status` 会提示"已启用但缺 --tray（登录会弹窗）→ 跑 startup on 重建"，
    `startup on` 同样带 `--tray`
- 界面文案同步："开机自启（计划任务 · 免 UAC · 静默进托盘）"

### 验证

三个行为实测（非提权测试副本 + 日志）：
无参数启动 → 有可见窗口；`--tray` → 进程存活、**无可见窗口**；重复 `--tray` → 静默退出（进程数仍为 1）。

## [0.4.3] - 2026-10-03

### 修复（磁盘温度读不到的真因）

- **`STORAGE_PROPERTY_ID` 不是连续枚举，我之前按连续编号推测，四个属性 ID 全部偏低 30**：
  官方头文件在 `StorageDeviceIoCapabilityProperty` 处**显式跳到 48**（reserved 占位），
  之后才是 49/50/51/52。发给驱动的属性 ID 是未定义值 → 一律返回 `ERROR_INVALID_FUNCTION`(1)。

  | 属性 | 旧（错） | 新（对） |
  | :--- | :---: | :---: |
  | `StorageAdapterProtocolSpecificProperty` | 19 | **49**（`0x31`，smartctl 源码同值） |
  | `StorageDeviceProtocolSpecificProperty` | 20 | **50** |
  | `StorageAdapterTemperatureProperty` | 21 | **51** |
  | `StorageDeviceTemperatureProperty` | 22 | **52** |

- 依据：[STORAGE_PROPERTY_ID 官方枚举](https://learn.microsoft.com/zh-cn/windows/win32/api/winioctl/ne-winioctl-storage_property_id)
  （页面上 `StorageDeviceIoCapabilityProperty:48`、`StorageDeviceSelfEncryptionProperty:64` 是显式锚点）
- 反证来自用户实测：**CrystalDiskInfo 9.2.3 能读到这块盘 35 ℃** —— 说明平台暴露温度，是调用方的问题
- `tools/disk-temp-probe.ps1` 同步改为正确属性 ID（52/51/50/49）

> 复盘：这次绕了 4 轮，根因是"按直觉补枚举"。教训 —— 涉及 Win32 枚举值，
> **必须查官方头文件/文档里的显式锚点**，不能靠"第几个字段就是第几个值"。

## [0.4.2] - 2026-10-03

### 新增 / 诊断

- **新增第 4 条磁盘温度通道：StorPort 适配器协议专用查询。**
  实测本机 NVMe 控制器跑的是微软自带 `stornvme`（不是 AMD RAID/VMD），
  但 `\\.\PhysicalDriveN` 上的三条通道在提权下仍返回 `err=1`（`ERROR_INVALID_FUNCTION`）——
  说明请求没被驱动认领。真实工具（smartctl / CrystalDiskInfo）对 NVMe 是把协议专用查询
  **发给 StorPort 适配器设备接口**（`GUID_DEVINTERFACE_STORAGEPORT`，本机枚举到 1 个：
  `\\?\pci#ven_1e49&dev_1031&...`），所以新增这条通道（`StorageAdapterProtocolSpecificProperty`=19）
  - 现在共 4 条：WMI 可靠性计数器 → 温度属性 IOCTL → NVMe 健康日志（设备）→ **StorPort 适配器健康日志**
- **新增诊断脚本 [tools/disk-temp-probe.ps1](tools/disk-temp-probe.ps1)**（管理员运行，只读）：
  一次性打印 5 条通道的 Win32 错误码 + WMI 实例数，用来区分"平台不暴露"与"我们调用方式不对"
- 磁盘温度的鼠标悬停提示现在把 4 条通道各自的失败原因全部列出

### 说明

错误码含义：`1`=`ERROR_INVALID_FUNCTION`（功能不被支持）、`5`=`ERROR_ACCESS_DENIED`（未提权）、
`87`=`ERROR_INVALID_PARAMETER`、`122`=`ERROR_INSUFFICIENT_BUFFER`。

## [0.4.1] - 2026-10-03

### 修复 / 增强

- **磁盘温度读不到时，界面直接说明原因**（不再只写"未实现"）：
  现在把三次尝试的失败原因拼进鼠标悬停提示，例如
  `可靠性计数器 0 条，DeviceId 没配上；温度属性 22 err=87；NVMe 健康日志 err=1`
- **新增 NVMe 专用通道**：`IOCTL_STORAGE_QUERY_PROPERTY` + `StorageDeviceProtocolSpecificProperty`(20)
  读 **NVMe 健康日志（SMART/Health，log page 0x02）** 的第 1~2 字节（开尔文温度）——
  这是 Windows 上读 NVMe 温度最通用的一条路（磁盘工具普遍用它）。
  失败时同时记录 `critical_warning` 以便判断
- **WMI 可靠性计数器的配对逻辑放宽**：`DeviceId` 对不上时，若全机只有一条计数器就直接采用
  （不同 Windows 版本的 DeviceId 取值格式不一致，之前会因此白白判为"没有"）
- 三档兜底顺序：WMI 可靠性计数器 → 温度属性 IOCTL → NVMe 健康日志 IOCTL，全部失败才显示"未实现"

## [0.4.0] - 2026-10-03

### 新增

- **核显温度、CPU die 温度、GPU 功耗与频率（零内核驱动）** —— 走 AMD 显卡驱动自带的
  **用户态 DLL `atiadlxx.dll` 的 ADL2 PMLog 接口**（`ADL2_New_QueryPMLogData_Get`），
  也就是 AMD Software: Adrenalin Edition「性能 → 指标」页读的同一套数据。**不需要管理员。**
  - 核显温度 `TEMPERATURE_GFX`(#28)、CPU die 温度 `TEMPERATURE_CPU`(#32)、SoC 温度 `TEMPERATURE_SOC`(#29)
  - GPU 功耗 `ASIC_POWER`(#23)、核显频率 `CLK_GFXCLK`(#1)、显存频率 `CLK_MEMCLK`(#2)、CPU 频率 `CLK_CPUCLK`(#34)
  - 温度行改以 SMU 真值为准；ACPI 热区降级为「热区温度」兜底（拿不到 SMU 时才显示）
  - 验证：8 线程负载下三个温度传感器同向上升（GPU 52→56、CPU 51→57、SoC 54→57 ℃），
    显存频率恒等于 DDR5-5600 的一半，CPU 频率与 PDH 估算互相印证
- **磁盘温度兜底通道**：WMI `MSFT_StorageReliabilityCounter` 不可用时，
  改用 `IOCTL_STORAGE_QUERY_PROPERTY`（`StorageDeviceTemperatureProperty` → `StorageAdapterTemperatureProperty`），
  并对返回值做单位兜底（>200 视为开尔文）
- **界面显示版本号**：窗口标题栏、顶部状态条第一行、托盘右键菜单顶部（不可点的加粗条目）、
  托盘悬停提示 —— 全部带上 `OpenMIFS v0.4.0`

### 更正

- **推翻此前"CPU die 温度拿不到、必须内核驱动"的结论**：AMD 的驱动自带用户态遥测通道，
  老接口（`ADL_Overdrive5/6_Temperature_Get`）在核显上确实失效，但新接口 PMLog 一直有值。
  docs/SENSORS.md 已重写相关章节并附传感器编号表
- `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA=61)` 返回 `STATUS_INVALID_PARAMETER`，
  未继续深挖（PMLog 已够用），记录在案

## [0.3.8] - 2026-10-03

### 文档

- **性能模式实测入档（`bench` 全核负载，每档 16 秒）**，并给出实用结论：

  | 值 | 标签 | 性能 % | 风扇峰值 RPM |
  | :---: | :--- | :---: | :---: |
  | 0 | 性能 | 95.6 | 5026 |
  | 1 | 均衡 | 96.7 | 4211 |
  | 2 | 低功耗 | 72.4 | 3134 |
  | 3 | 满速 | 83.9 | 3143 |

  - **值 1（均衡）是甜点档**：性能与值 0 在噪声内相同，但风扇峰值低 815 RPM → 日常推荐
  - 值 2 最凉，代价约 25% CPU
  - **值 3（满速）被忽略**：数据与值 2 基本一致，与"满速被 Type-C 门控"的结论互相印证
- README 功能表与 [docs/TESTED-MODELS.md](docs/TESTED-MODELS.md) 同步

## [0.3.6] - 2026-10-03

### 修复

- **`.\mifs.ps1 fan` 被参数校验拒掉**：0.3.5 给 CLI 加了 `fan` 的 switch 分支，却漏了把它加进 `ValidateSet`，
  于是 `mifs.ps1 fan test` 直接报「参数不属于 ValidateSet 集合」。已补上并**逐个动作冒烟验证**
  （status / probe / test / scan / raw / osd status / osd diagnose / startup status / log /
  sensors / sensors probe / fan test / mode / fanboost / kbd 共 15 个动作全部通过）
- `fan test` 在非管理员下会把 `$null` 的供电类型显示成"电池供电"，改为"读不到（需要管理员）"

### 新增

- CI 增加「CLI 动作一致性检查」：解析 `mifs.ps1` 的 `ValidateSet` 与主 `switch` 分支，
  任何分支不在 ValidateSet 内就构建失败 —— 这次的漏检以后不会再发生
  （已做反向验证：故意去掉 `fan` 能检出）

## [0.3.5] - 2026-10-03

### 修复

- **风扇满速的判定方式错了**：之前用「`fn=20` 寄存器读回值是否变化」判定，
  但 EC 完全可能**不更新寄存器镜像却照样执行**。改为**按风扇转速判定**：
  - exe 点「风扇满速」时，读回没变就自动采样 4 秒转速；转速上去（≥250 RPM）即判定可用
  - 命令行新增 **`.\mifs.cmd fan test`**：基线 4 次 → 开满速 → 采样 8 秒 → 报转速差与寄存器读回 →
    自动恢复关闭。全程可逆，适合把结论钉死
  - `Mifs.FanPeak(ms, fallback)`：轮询转速取峰值，供两处复用

### 说明

- 本机**只有 USB-C 供电口**，因此上游文档要求的"圆口 DC 供电"前提无法满足；
  但"EC 是否真的不执行"只能靠转速验证来回答 —— 见 [docs/FAN-CONTROL.md](docs/FAN-CONTROL.md)
- 若转速验证不通过：MIFS 层被电源类型卡死，只剩 EC RAM（PawnIO 等第三方签名驱动）一条路，
  默认不做（要装内核驱动 + 盲写 EC 寄存器，风险与收益不成比例）

## [0.3.4] - 2026-10-03

### 修复 / 更正

- **更正供电类型语义（这是个会误判功能的坑）**：功能号 `19` 不是"1=外接电源 / 0=电池"，
  而是上游 [tongfang-mifs-wmi 驱动文档](https://lkml.iu.edu/hypermail/linux/kernel/2602.0/08303.html) 写明的
  **`1` = Type-C(PD)、`2` = 圆口 DC**。状态区与 `mifs.ps1 status` 已按真实语义显示。
- **风扇满速的"未实现"结论很可能是误判**：同一份上游文档写明 ——
  *"性能/满速模式在 Type-C 供电下不可用"*（驱动代码里对电池与 Type-C 都返回 `EOPNOTSUPP`）。
  本机当时读到 `fn=19 = 1`（Type-C），EC 拒绝写入属于**硬件按电源类型门控**，不是机型不支持。
  现在 Type-C 供电下点「风扇满速」会明确提示"换个电源再试"，而不是永久判定"未实现"。
- **能力缓存与电源类型绑定**：`capabilities.txt` 新增 `fanboost_actype`，
  换了电源（Type-C ↔ 圆口 ↔ 电池）会自动重新探测，不再拿旧结论误判。

### 新增

- [docs/FAN-CONTROL.md](docs/FAN-CONTROL.md)：**风扇调速可行方案分析**
  - 上游驱动文档明确 `fn=20` = 手动风扇控制开关（`[4]` 风扇组 / `[5]` 状态）、
    `fn=21` = **PWM 占空比**，即 MIFS 本身就带调速接口
  - 三条路线：① 插圆口 DC 电源复测（成本≈0，可能就是全部答案）
    ② 手动 PWM 占空比（取值域未知，需谨慎探测）③ 直接写 EC RAM（需内核驱动，不推荐）
  - 同时记录：Windows 电源策略里本机**没有** SYSCOOLPOL（系统散热策略），
    即没有 OS 级的间接风扇策略可用

## [0.3.3] - 2026-10-03

### 新增 / 变更

- **自带屏幕提示改为「自动让位」，解决与官方 OSD 重复显示的问题**
  （用户反馈：官方 OSD 修好后，按 Fn 会同时出现官方提示和 OpenMIFS 的提示）
  - 新增三档模式（exe 主界面「启动与 OSD → 屏幕提示」下拉框，设置存 `settings.txt`）：
    - **自动（默认）**：官方 OSD 的提示窗此刻可见就让位，不重复显示
    - **总是显示**：不管官方 OSD，总显示自带的（官方又坏掉时用）
    - **关闭**：完全不显示自带提示
  - 判定方式：枚举顶层窗口，找标题为 `FloatingNativeWindow`、`WS_VISIBLE` 且尺寸合理的
    那个分层窗（就是 `BLDFnHotkeyUtility.exe` 画提示用的窗口）
  - 外部改动（按 Fn）延迟 **350 ms** 再判定 —— 让官方 OSD 先把提示画出来，避免抢跑
  - 判定结果写日志：`官方 OSD 正在显示，跳过自带提示：性能模式 · 均衡`
- **传感器读取移出 UI 线程**：首次读取要预热 PDH + 走 WMI（实测约 1.8 s），
  原来会卡住窗口；现在在线程池里读、回到 UI 线程渲染，日志会标明耗时（`tid` 不再是主线程）

### 说明

- 为什么不用「官方 OSD 健康度」来自动关闭自带提示：实测过 ——
  服务在跑、进程在跑、`OSDEvents` 也在收 Fn 事件，但屏幕上什么都不画。
  也就是说"组件健康"不等于"真的画出来了"，只有**窗口是否可见**能反映实际情况。

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