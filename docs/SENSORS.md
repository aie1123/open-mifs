# 传感器：能读到什么、怎么读（可行性分析）

> **状态：0.3.0 已实现**。exe 的「传感器」选项卡、`.\src\mifs.ps1 sensors`、
> `OpenMIFS.exe --sensors` 都用本文这套数据源。实现踩到的坑见第 3 节。
>
> 结论先行：**CPU 功耗、CPU 邻近热区温度、GPU 利用率、内存规格、风扇转速、电池 —— 这些在 Windows 上免驱动就能读，本机已实测。**
> CPU 核心温度（Tctl/Tdie）、主板/VRM/内存温度则**必须**有内核驱动，本项目不应为了它们破坏"零驱动、零依赖"的定位。
>
> 本文所有"实测"数据都来自 MECHREVO WUJIE14 PRO（R7-7840HS / Radeon 780M / Win11 24H2），
> 采集方式写在每一行里，可复现。

---

## 1. 实测结果总表

### ✅ 免驱动、本机已实测可用

| 组件 | 指标 | 数据源 | 实测值 / 标定 |
| :--- | :--- | :--- | :--- |
| CPU | **封装功耗（RAPL）** | PDH `\Energy Meter(rapl_package0_pkg)\Power` | 空闲 **10.8 W** → 8 线程满载 **35.8 W**（单位 mW，已用负载标定） |
| CPU | 每核功耗 | `\Energy Meter(rapl_package0_coreN_core)\Power` | 0.12 ~ 1.1 W/核 |
| CPU | SoC / VDDCR 域功耗 | `vddcr_soc power` / `vddcr_vdd power` | 1.6 W / 3.6 W |
| CPU | 插槽总功耗 | `current socket power` / `apu power` | 7.9 W → 31.6 W |
| CPU | **有效频率** | `\Processor Information(_Total)\Processor Frequency` × `% Processor Performance` | 3420 MHz × 102.7% ≈ **3.5 GHz** |
| CPU | 负载 | `\Processor Information(_Total)\% Processor Time` | 18.7% |
| CPU 邻近 | **热区温度** | PDH `\Thermal Zone Information(_tz.tz01)\Temperature` | 空闲 **52.9 ℃** → 满载 **64.9 ℃**（K 为单位；随负载上升，是真实可用的温度） |
| CPU 邻近 | 高精度温度 | `\Thermal Zone Information(*)\High Precision Temperature` | 3262（0.1 K 单位 = 53.0 ℃） |
| CPU 邻近 | 降频原因 | `\Thermal Zone Information(*)\Throttle Reasons` | 0（位域，非 0 = 正在因过热/功耗降频） |
| GPU | **利用率**（按引擎/进程） | PDH `\GPU Engine(*)\Utilization Percentage` | 取到 3D 引擎 5.68%、Copy 0.14% 等 |
| GPU | 显存占用 | `\GPU Adapter Memory(*)\Dedicated Usage` | 398 MB（3 个 LUID：核显 + 2 个虚拟适配器） |
| 内存 | 容量 / 型号 / 速率 | WMI `Win32_PhysicalMemory` | 2 × 16 GB，DDR5（SMBIOSMemoryType=34），**5600 MT/s** |
| 风扇 | 双风扇转速 | MIFS `fn=13`（项目已有） | ~2500 RPM |
| 电池 | 健康度 / 容量 / 收发功率 | `powercfg` + WMI `Win32_Battery`（项目已有） | 见 `docs/TESTED-MODELS.md` |
| 存储 | 盘型号 / 类型 / 健康 | `Get-PhysicalDisk` / `Win32_DiskDrive` | YMTC PC300-1TB-B，NVMe，Healthy |

### ⚠️ 接口存在，但本机取不到数据（0.3.0 实测结论）

| 目标 | 途径 | 结论 |
| :--- | :--- | :--- |
| NVMe **温度** | `MSFT_StorageReliabilityCounter`（`Temperature`/`Wear`/`PowerOnHours`，按 `DeviceId` 与 `MSFT_PhysicalDisk` 配对） | 非管理员报「拒绝访问」；**exe 已提权，应可用**（面板上会显示具体错误，便于确认） |
| GPU **温度 / 频率** | P/Invoke `atiadlxx.dll`：`ADL_Main_Control_Create` + `ADL_Overdrive5_Temperature_Get` / `ADL_Overdrive6_Temperature_Get` | ❌ **本机取不到**：ADL 能加载、接口都存在，但 5 个适配器都返回不支持（Radeon 780M 核显）。面板如实显示「未实现」，不做假数据 |
| GPU 温度（未试的备选） | `gdi32!D3DKMTQueryAdapterInfo` + `KMTQAITYPE_ADAPTERPERFDATA` | 未验证；比 ADL 更通用（NVIDIA/Intel/AMD 同一套），但同样取决于驱动是否实现 |

### ❌ 免驱动拿不到（别绕）

| 目标 | 为什么拿不到 |
| :--- | :--- |
| CPU 核心温度 Tctl/Tdie | AMD 走 SMU 邮箱（PCI 0:0.0 索引端口），用户态碰不到；ACPI 热区只有 1 个 `_tz.tz01`，是 EC 上报的板级/封装邻区温度，不是 die 温度 |
| 主板 / VRM / 供电模块温度 | 在 EC 的传感器表里，需要直接读 EC RAM（内核驱动） |
| 内存温度 | SPD Hub（SMBus），需要驱动 |
| 独立显卡 | 本机没有独显 |

---

## 2. 三条候选路线（按"是否破坏零驱动定位"排序）

### 方案 A：纯 Windows 原生 —— 推荐，作为 v0.3.0 主线

只用三样东西，全部已在系统里、全部可被 .NET Framework 4.8 直接调用：

| 通道 | API | 备注 |
| :--- | :--- | :--- |
| 性能计数器（PDH） | `System.Diagnostics.PerformanceCounter`（`System.dll`，无需新增引用） | 覆盖 CPU 功耗/频率/负载、热区温度、GPU 利用率/显存 |
| WMI / CIM | `System.Management`（项目已在用） | 内存、磁盘、电池 |
| MIFS | 项目已有 | 风扇转速、Fn 锁等 |

- **权限**：PDH 与 ACPI 热区**非管理员也能读**（本文所有实测都是在非管理员会话里拿到的）→ 传感器模块不依赖提权。
- **覆盖度**：约 70% 的"控制中心该显示的参数"，且每一条都是**能标定的真值**。
- **代价**：探测命令约 1~2 小时；CLI + exe 传感器页约半天到一天。
- **风险**：计数器名与实例名因机型而异（`rapl_*`、`_tz.tz01` 都是 AMD/具体 BIOS 的产物）→ 必须像 MIFS 的 `probe`/`test` 一样做**运行时探测 + 能力缓存**，读不到就明确显示"未实现"，不要猜。

### 方案 B：ADL P/Invoke，补 GPU 温度/功耗/频率 —— 可选增强

- **不打包任何 AMD 二进制**：调用系统里已安装的 `C:\Windows\System32\atiadlxx.dll`，MIT 许可无污染。
- 先做**能力探测**（一次调用，失败即整块隐藏），成功才在界面上出现 GPU 温度/功耗。
- 备选实现是 `D3DKMTQueryAdapterInfo(KMTQAITYPE_ADAPTERPERFDATA)`，比 ADL 更通用（NVIDIA/Intel/AMD 同一套），但同样取决于驱动。
- **风险**：APU 上可能返回不支持 —— 属于"花 2~4 小时可能白做"的投入，建议**先探测再决定**。

### 方案 C：自带/捆绑内核驱动 —— 不做

| 候选 | 否决理由 |
| :--- | :--- |
| WinRing0（`inpout`/RyzenAdj 系） | 已被微软列入**易受攻击驱动黑名单**，Defender 与本机的火绒都会拦；公开 MIT 仓库分发它还涉及签名与法律责任 |
| PawnIO / LibreHardwareMonitor 驱动 | 虽是签名驱动，但仍是第三方内核组件，与 README 现在承诺的"**不安装任何官方组件、不加载任何驱动、不需要第三方运行库**"直接冲突 |
| 自己写签名驱动 | 成本与维护完全不成比例 |

### 方案 D：桥接用户自己装的监控工具 —— 可选，写清是"外部依赖"

本机**没有**装 LibreHardwareMonitor / HWiNFO / OpenHardwareMonitor（`root\LibreHardwareMonitor`、
`root\OpenHardwareMonitor`、`root\HWiNFO` 三个 WMI 命名空间都不存在）。

如果用户愿意自己跑 LibreHardwareMonitor，它会把全部传感器（含 Tctl、主板、VRM）挂到
`root\LibreHardwareMonitor` 的 `Sensor`/`SensorValue` 上，读起来不到 20 行代码。

- 定位：**可选数据源**，不进主链路；README 写明"想看 CPU 核心温度请自行安装并运行 LHM，OpenMIFS 只读它的 WMI"。
- 代价：约 1 小时。

---

## 3. 实施要点（踩坑清单，0.3.0 实现时全部踩过）

0. **PDH 速率类计数器必须先预热**。`\Energy Meter(*)\Power`、`% Processor Time` 这类计数器
   第一次读会返回 **0**：它们靠两次采样之间的时间差算值。
   正确做法：一次性把所有要用的计数器建好、各采一次，然后**统一等一个采样窗口（实现里用 700 ms）**，
   之后每次读都是有效值。逐条"建了就读"会得到一堆 0；每条各等一次则慢得没法用。
   实测对比：不预热时封装功耗显示 `0.00 W`，预热后是 `24.82 W`。
1. **单位必须标定，不能猜。**
   `\Energy Meter(*)\Power` 是**毫瓦**：本机空闲 10773 → 8 线程满载 35757，即 10.8 W → 35.8 W。
   热区 `Temperature` 是**开尔文**（326 K = 52.9 ℃），`High Precision Temperature` 是**0.1 K**。
2. **不要用累计的 `Energy` 计数做差分来算功率。** 它从开机起累计且是单精度浮点，
   两次读数相差 11 秒时差值被舍入误差淹没（实测 ΔE/Δt 与 Power 差 200 倍以上），直接用 `Power`。
3. **一次批量取，别循环单取。** `Get-Counter -Counter a,b,c` 一次拿全部，比逐条快一个数量级；
   C# 里 `PerformanceCounter` 对象要缓存复用。
4. **实例名要探测并缓存**：`RAPL_Package0_PKG`、`\_TZ.TZ01` 这类名字是机型/BIOS 相关的，
   必须运行时枚举（`PerformanceCounterCategory.GetInstanceNames()`），不能写死。
5. **热区只有一个也算有用**：本机 `_tz.tz01` 随负载 52.9 → 64.9 ℃，可用于"是否过热"和风扇策略判断；
   但**不要把它标成"CPU 温度"**，标成"热区/封装邻区温度"才诚实。
6. **`\Power Meter` 本机全为 0**（该计数器集存在但不填充），不要依赖它拿整机功耗；
   整机近似值用 `current socket power` 或电池侧数据。
7. **GPU 百分比要用 WMI 的「已格式化」类**：`PerformanceCounterCategory.ReadCategory()` 只返回原始值，
   `PERF_RAW_FRACTION` 类计数器拿不到 Base，算不出百分比。
   用 `Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine` /
   `..._GPUAdapterMemory` 最省事（Windows 已经替你算好了）。
8. **磁盘温度按 `DeviceId` 配对磁盘**：`MSFT_StorageReliabilityCounter` 有 `DeviceId`，
   与 `MSFT_PhysicalDisk.DeviceId` 一致。WQL `ASSOCIATORS OF` 在 `ObjectId` 含引号时会报「无效查询」，
   不要用它。
9. **传感器与 MIFS 控制分开**：数据源、失败模式、刷新代价都不同，
   exe 里做成独立选项卡，1~2 秒刷新，失败时整块显示"未实现"而不是弹错。
   只在页面可见时采样，后台不采。
10. **别动定位**：新增传感器之后 README 的"零驱动、零依赖"依然成立 —— 这正是选择方案 A 的原因。

---

## 4. 怎么证明读到的数是真值（本项目一贯的验证纪律）

| 方法 | 做法 | 本机已做 |
| :--- | :--- | :--- |
| 负载对照 | 空闲 vs 8 线程满载，看数值是否同向、幅度是否合理 | ✅ 功耗 10.8→35.8 W，热区 52.9→64.9 ℃ |
| 跨源交叉 | RAPL 封装功耗 vs `vddcr_vdd + vddcr_soc`；GPU 利用率 vs 3D 引擎实例 | ✅ 量级一致 |
| 模式对照 | 低功耗/均衡/性能三档跑同一负载（复用 `bench`），看功耗墙差异 | 待做 |
| 外部对照 | 装一次 HWiNFO/LHM，交叉核对包功耗与 Tctl 偏差 | 待做（可选） |

---

## 5. 落地状态

| 步骤 | 状态 |
| :--- | :--- |
| 1. `sensors probe`（只读探测） | ✅ 0.3.0：`.\src\mifs.ps1 sensors probe` / `OpenMIFS.exe --sensors` |
| 2. 传感器面板（方案 A 全量） | ✅ 0.3.0：exe「传感器」选项卡 + `.\src\mifs.ps1 sensors` |
| 3. 方案 B 探测（ADL / D3DKMT） | ⚠️ ADL 已试 → 本机核显取不到温度（如实显示「未实现」）；D3DKMT 未试 |
| 4. 方案 D 桥接（LibreHardwareMonitor WMI） | ⬜ 未做（可选，本机没装 LHM） |

本机 0.3.0 实测读数样例（非管理员，2026-10-03）：

```
封装功耗   : 24.82 W      ← PDH Energy Meter / RAPL_Package0_PKG
有效频率   : 2.72 GHz     ← 3135 MHz × 86.6%
负载       : 16.8 %
核心功耗 W : 1.97 / 2.39 / 1.43 / 1.64 / 3.07 / 1.39 / 0.92 / 1.04（8 个核域）
VDDCR / SoC: 13.78 W / 6.53 W      插槽功耗 : 25.00 W
热区       : 62.9 ℃（高精度 63.1 ℃，降频原因 0）
GPU 利用率 : Video Codec 14.0 %（合计 21.0 %）    显存 : 360 MB
内存       : 32.0 GB  2×16 GB DDR5-5600   占用 15.9 / 31.2 GB（51.0 %）
存储       : YMTC PC300-1TB-B（SSD）NVMe 健康 954 GB（温度需管理员）
电池       : 92 %  外接电源  健康 100.0 %（65000/65000 mWh）
```

一眼可见的"真值"判据：**热区温度在跑负载时会从 ~53 ℃ 升到 ~65 ℃，封装功耗从 ~11 W 升到 ~36 W。**

---

## 6. 一句话总结

**这套机器上"能免驱动读到的比想象的多"**：CPU 封装功耗（RAPL）、随负载变化的热区温度、
GPU 利用率、内存速率、风扇转速全都拿得到；真正缺的只有 **CPU die 温度、主板/VRM/内存温度**，
而它们无一例外需要内核驱动 —— 为了它们破坏"零驱动"的定位不划算，
需要时桥接用户自己装的 LibreHardwareMonitor 即可。
