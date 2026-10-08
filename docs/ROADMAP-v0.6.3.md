# v0.6.3 实施计划（三项硬件自适应性 + 托盘图标 GPU 温度）

> 落点是**已经调研过**的，不是猜的。每项按项目纪律验证（asInvoker 副本 / 合成数据 / 量测），
> 改完先交作者本地替换测试，**只有作者说"推送"才 commit + tag + 发 Release**（见 AGENTS.md §6.1）。

---

## C. 托盘图标支持 GPU 温度（最小、独立，先做，约 30 分钟）

**现状**：`TrayIcon.Kinds = {none, cput, cpup, cpul}`，只能显示 CPU 侧指标。

| 改动 | 位置 |
| :--- | :--- |
| `Kinds` 增 `gput`；`KindLabels` 增「GPU 温度」 | `OpenMIFS.cs` `TrayIcon` |
| 阈值：新增 `_tGpuTemp`（默认 `55/70/85`，与 CPU 温度同档），settings 键 `tray_icon_gt` | `TrayIcon.LoadThresholds/SaveThresholds/ThresholdText/ResetThresholdFor/ThresholdsOf` |
| `TrayIcon.Number(kind, out level)` 增 `gput` 分支，数值取「GPU 温度」读数 | `TrayIcon` |
| 面板：偏好设置「变色阈值」行按图标数据源切换（现有 contextual 机制）自动支持，只需把 `gput` 计入 `Kinds` | `MainForm.RefreshTrayThresholdRow` |
| 数据源：GPU 温度来自 ADL PMLOG（`Sensors`），**隐藏态**要能取到 → 走 `RequestHiddenSensorSnapshot()` | `MainForm` / `TrayContext` |
| 单位后缀：`℃`；两字符数值 + 度数符号在 20px 图标里要验证可读 | `TrayIcon.Render` |

**验证**：`--icon-preview` 出图对比四个档位配色；提权实例上切到 `gput` 看图标数值与 ToolTip。

---

## B. 多风扇 / 单风扇自适应（约 40 分钟）

**现状**：关键读数第三列**固定** `fans[0]` + 说明「风扇1」；明细区**硬编码**一行 `风扇2`
（`_rows[0]`，由 `RefreshAll` 供数、`RefreshSensors` 按名字跳过）。

| 改动 | 位置 |
| :--- | :--- |
| 先拿到真实风扇路数：`Mifs.GetFans()` 返回 `int[3]`，`fn=13` 第 3 路通常是 0；按**非零个数**决定布局 | `MainForm.RefreshAll` |
| 明细区新增 `ReadoutRow.Visible` + 布局时紧凑掉不可见行（**这一层 B 和 A 共用**） | `Ui.cs` `ReadoutRow`、`MainForm.LayoutAll` |
| 1 个风扇：关键读数说明显示 `风扇`（不带 1），明细区**不出现**风扇行 | — |
| 2~3 个风扇：关键读数 `风扇1`，明细区依次 `风扇2`（`风扇3` 若非零） | — |
| 托盘提示 `fan` 项按实际数量拼（`风扇 3075/3194`，单风扇 `风扇 3075`） | `TrayText` |

**验证**：合成注入 1 路 / 2 路 / 3 路三种情形，确认关键读数说明与明细行数正确、无空行。

---

## A. 多硬盘信息显示（最大，约 60 分钟）

**现状**：`Sensors.cs` 已查 `MSFT_PhysicalDisk`（**本就返回多行**：FriendlyName/MediaType/BusType/
Size/DeviceId）与 `MSFT_StorageReliabilityCounter`，但**只取一条**，输出单行 `磁盘` + `磁盘温度`。
每盘温度的两个函数**已按盘号参数化**：`DiskTemperatureIoctl(driveIndex)`、`NvmeTemperatureIoctl(driveIndex)`。

| 改动 | 位置 |
| :--- | :--- |
| 遍历所有物理盘 → 每盘输出 `磁盘N`（值=容量，型号/接口进 Note）+ `磁盘N 温度` | `Sensors.cs` 磁盘段（约 640~740 行） |
| DeviceId ↔ 盘号对应：优先 `MSFT_PhysicalDisk.DeviceId` 的 `PDx` 解析，失败则按枚举顺序兜底，并在 Note 里写明用了哪条路径 | 同上 |
| 明细区槽位：预建 4 个（`磁盘1`/`磁盘1 温度` … `磁盘4`），配合 `ReadoutRow.Visible` 隐藏多余槽位 | `MainForm.BuildUi` 簇规格 + `LayoutAll` 行索引 |
| 单盘机器：仍是 `磁盘温度` + `磁盘` 两行，**不出现** `磁盘2` 空行 | 由可见性机制保证 |
| 托盘提示 `磁盘` 项：多盘时按 `磁盘1 32℃/磁盘2 45℃` 精简（受 62 字符上限约束） | `TrayText` |

**验证**：合成注入第二块盘（改测试副本的 WMI 结果），确认两行排布、ToolTip 型号、温度取值路径；
真机多盘要作者插盘后再实测（本机只有 YMTC PC300-1TB-B 一块）。

---

## 顺序与依赖

```
C（独立，30min）→ B（引入"行可见性"机制，40min）→ A（复用该机制 + 遍历磁盘，60min）
```

- B 与 A 共用 `ReadoutRow.Visible` + 布局紧凑 → **先 B 后 A**，A 不必再动布局层
- 每一项目完成后单独构建、单独交作者验收；三项都通过后可一起发 v0.6.3
- 版本号/CHANGELOG 随每项写好但不提交，发布时一次成型
