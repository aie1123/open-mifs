# OpenMIFS

**不装官方控制中心的同方 MIFS 控制台 —— 单文件 PowerShell，零依赖。**

机械革命（MECHREVO / 同方 TongFang）有一部分机型的 BIOS 暴露了 **MIFS（MiInterface）** ACPI WMI 接口，
官方控制中心正是通过它来控制性能模式、风扇、键盘背光等硬件。

问题是：**很多机型官方压根没出过控制中心**（例如无界 14 Pro 2023），
于是这些接口就一直闲置，用户什么也调不了。

OpenMIFS 直接调用这个接口，不安装任何官方组件、不加载任何驱动、不需要第三方运行库。
一个 `.ps1` 文件就是全部。

```
mifs-gui.cmd        ← 双击这个，图形界面
mifs.cmd status     ← 命令行版
```

---

## 功能

### 图形界面（`mifs-gui.ps1`）

| 区域 | 说明 |
| :--- | :--- |
| 性能模式 | 低功耗 / 均衡 / 性能 一键切换，当前档位用 `●` 标记 |
| 风扇 | 各风扇实时转速（RPM） |
| 硬件开关 | Fn 锁、触控板锁定 |
| 键盘背光 | 亮度 0~3 档 |
| 风扇满速 | 一键强冷（机型支持时） |
| 状态面板 | 供电状态、CPU 温度/功率、接口与调用方式 |
| 自动刷新 | 可调 2 / 3 / 5 / 10 秒 |

未实现的功能会自动置灰并标注「未实现」，不会假装能用。

### 命令行（`mifs.ps1`）

```powershell
.\src\mifs.ps1 status               # 显示状态（默认）
.\src\mifs.ps1 probe                # 诊断调用链（排错先跑这个）
.\src\mifs.ps1 test                 # 逐个探测驱动清单里的功能号
.\src\mifs.ps1 scan 63              # 扫描功能号 0..63，找出所有可用的
.\src\mifs.ps1 bench                # 实测各档功耗高低，校正模式映射（约 2 分钟）
.\src\mifs.ps1 mode low             # 切模式：low / balanced / performance
.\src\mifs.ps1 fanboost on          # 风扇满速开关
.\src\mifs.ps1 kbd 0                # 键盘背光 0~3
.\src\mifs.ps1 raw 19               # 对指定功能号发 GET，打印原始字节
```

`status` / `probe` / `test` / `scan` / `raw` 只发 GET，纯读，不改任何状态。
`mode` / `fanboost` / `kbd` 会写 EC 寄存器，都是官方定义的可逆开关。

---

## 实测机型与能力矩阵

| 功能号 | 功能 | 无界 14 Pro 2023（R7-7840HS） |
| :---: | :--- | :---: |
| 8 | 性能模式 | ✅ |
| 10 | 键盘类型 | ✅ |
| 11 | Fn 锁 | ✅ |
| 12 | 触控板锁 | ✅ |
| 13 | 风扇转速 | ✅ 双风扇 |
| 18 | 键盘背光亮度 | ✅ |
| 19 | 供电状态 | ✅ |
| 20 / 21 | 风扇满速 | ⚠️ 接口响应但写入被忽略 |
| 9 | GPU 模式 | ❌ 无独显 |
| 16 / 17 | RGB 模式 / 颜色 | ❌ 白光键盘 |
| 22 / 23 | CPU 温度 / 功率 | ❌ |

详细实测记录见 [docs/TESTED-MODELS.md](docs/TESTED-MODELS.md)。

---

## 已知限制

1. **无法用软件限制充电到 80%。** MIFS 接口的功能号里没有电池充电阈值，
   本工具、官方控制中心、任何第三方工具在这类机型上都做不到。
   只能进 BIOS 找相关选项，或者手动拔插适配器。
2. **无法调节风扇转速。** MIFS 的 `13` 号功能是**只读**的转速上报。
   上游 Linux 驱动能调速是因为它直接读写 EC RAM（`0x0f00-0x0f5f` 风扇曲线表、
   `0x1804`/`0x1809` 直写转速），走的不是 MIFS WMI。
   用 `scan` 可以找找有没有未公开的功能号，但目前没有发现。
3. **不同机型的功能号含义可能不同。** 功能号来自 Linux 内核驱动
   `tongfang-mifs-wmi` 的逆向结果，厂商并未公开。如果发现对不上，
   用 `bench` 实测校正，并欢迎提 issue 反馈。
4. **性能模式的值编码可能因机型而异。** 上游驱动标注 `0=均衡 1=性能 2=低功耗`，
   但无界 14 Pro 2023 实测是 `0=性能 1=均衡 2=低功耗`。
   映射写在脚本顶部，可自行修改。

---

## 系统要求

- Windows 10 / 11
- Windows PowerShell 5.1（系统自带）或 PowerShell 7+
- **管理员权限**（ACPI WMI 方法调用必需）
- 机型 BIOS 暴露了 MIFS 接口（用 `probe` 确认）

无需安装任何组件。不需要 .NET SDK、不需要 Rust、不需要官方控制中心。

---

## 快速开始

```powershell
git clone https://github.com/aie1123/open-mifs.git
cd open-mifs
```

**图形界面**：双击 `mifs-gui.cmd`，UAC 弹窗点「是」。

**命令行**：

```powershell
.\mifs.cmd status
.\mifs.cmd test
.\mifs.cmd mode performance
```

如果直接运行 `.ps1` 被执行策略拦住：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\src\mifs.ps1 status
```

---

## 工作原理

```
ACPI 设备       ACPI\PNP0C14\MIFS
Windows WMI 类  root\wmi:MICommonInterface   （实例名 ACPI\PNP0C14\MIFS_0）
方法            MiInterface(InData[32]) -> OutData[30]
ACPI GUID       {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}

请求  InData[0]=00  [1]=类型(250 GET / 251 SET)  [2]=00  [3]=功能号  [4..]=参数
响应  OutData[0]=00 [1]=80                      [2]=00  [3]=功能号回显  [4..]=数据
```

协议细节、踩过的坑、以及怎么发现的，见 [docs/PROTOCOL.md](docs/PROTOCOL.md)。

---

## 常见问题

**Q：`probe` 说 WMI 类不存在？**
说明你的机型 BIOS 没有暴露 MIFS 接口，本工具不适用。

**Q：`probe` 说「无效的方法参数」？**
ACPI WMI 方法必须在**实例**上调用，不能在类上调用。本工具已处理这一点；
如果你在自己写代码，记得用 `Invoke-CimMethod -InputObject <实例>`。

**Q：模式切换重启后还在吗？**
部分机型 EC 会保存，部分会被 BIOS 重置。重启后跑一次 `status` 即可确认。

**Q：切了模式感觉没变化？**
跑 `bench` 实测。它会给出各档的 CPU 性能百分比和风扇峰值，用数据说话。

**Q：会不会把电脑搞坏？**
本工具只使用 MIFS 接口公开的功能号，且都是官方定义的可逆开关，不写未知寄存器。
但请注意：**不要用 `raw` 对未知功能号发 SET**（脚本本身也不提供这个能力）。

---

## 贡献

最有价值的贡献是**你的机型的实测数据**。请跑：

```powershell
.\mifs.cmd test
.\mifs.cmd probe
```

把完整输出贴进 issue，并附上机型全称、CPU、BIOS 版本。我会汇总进
[docs/TESTED-MODELS.md](docs/TESTED-MODELS.md)。

机型信息获取方式：

```powershell
Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\BIOS' |
  Select-Object SystemProductName, BIOSVersion, BIOSReleaseDate, BaseBoardProduct
```

---

## 免责声明

本项目为社区独立项目，**与机械革命（MECHREVO）/ 同方（TongFang）无任何关联**，
非官方出品。功能号与协议来自对公开 WMI 接口的观察和上游 Linux 内核驱动的公开信息，
不包含任何厂商专有代码或逆向数据。

作者不对使用本工具造成的硬件损坏或数据丢失承担责任。
所有写操作都是官方定义的可逆开关，但仍请自行判断风险。

## 许可

[MIT](LICENSE)
