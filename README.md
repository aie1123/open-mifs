# OpenMIFS

**不装官方控制中心的同方 MIFS 控制台 —— 单文件 exe，零依赖。**

机械革命（MECHREVO / 同方 TongFang）有一部分机型的 BIOS 暴露了 **MIFS（MiInterface）** ACPI WMI 接口，
官方控制中心正是通过它来控制性能模式、风扇、键盘背光等硬件。

问题是：**很多机型官方压根没出过控制中心**（例如无界 14 Pro 2023），
于是这些接口就一直闲置，用户什么也调不了。

OpenMIFS 直接调用这个接口 —— 不安装任何官方组件、不加载任何驱动、不需要第三方运行库。

```
OpenMIFS.exe        ← 下载即用，单文件 50 KB，常驻任务栏通知区域
mifs-gui.cmd        ← 从源码直接跑图形界面
mifs.cmd status     ← 从源码跑命令行版
```

---

## 三种形态

| 形态 | 文件 | 适合谁 |
| :--- | :--- | :--- |
| **托盘 exe** ⭐ | `OpenMIFS.exe` | 日常使用。单文件、免安装、常驻托盘，托盘菜单直接切模式 |
| 图形脚本 | `mifs-gui.cmd` | 不想跑 exe，或想改界面 |
| 命令行 | `mifs.cmd` | 脚本化、排查问题、提交机型数据 |

三种形态调用同一套接口，功能完全一致。

---

## 功能

### 托盘 exe / 图形界面

| 区域 | 说明 |
| :--- | :--- |
| 性能模式 | 低功耗 / 均衡 / 性能 一键切换，当前档位用 `●` 标记 |
| 风扇 | 各风扇实时转速（RPM） |
| 硬件开关 | Fn 锁、触控板锁定 |
| 键盘背光 | 亮度 0~3 档 |
| 风扇满速 | 一键强冷（机型支持时） |
| 状态面板 | 供电状态、CPU 温度/功率、接口与调用方式 |
| 自动刷新 | 可调 2 / 3 / 5 / 10 秒 |

exe 版本额外具备：

- **常驻托盘**：关闭窗口 = 最小化到通知区域，程序继续后台运行
- **托盘菜单**：右键托盘图标可直接切性能模式、开关风扇满速，不用打开主界面
- **托盘提示**：鼠标悬停显示当前模式与风扇转速
- **单实例保护**：重复启动会提示，不会重复占用资源
- **自动提权**：清单里声明了 `requireAdministrator`，双击即弹 UAC，不用手动右键

未实现的功能会自动置灰并标注「未实现」，不会假装能用。

### 命令行

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
   映射写在 `src/mifs.ps1`、`src/mifs-gui.ps1`、`src/csharp/OpenMIFS.cs` 顶部，可自行修改。

---

## 系统要求

| | 要求 |
| :--- | :--- |
| 系统 | Windows 10 1809+ / Windows 11 |
| 运行时 | 系统自带 **.NET Framework 4.8**（Win10 1809+ 与 Win11 均内置，无需安装） |
| 权限 | **管理员**（ACPI WMI 方法调用必需，exe 已自动提权） |
| 硬件 | 机型 BIOS 暴露了 MIFS 接口（用 `probe` 确认） |

编译 exe 不需要 .NET SDK、不需要 Visual Studio、不需要联网。

---

## 快速开始

### 方式一：直接下载 exe（推荐）

到 [Releases](https://github.com/aie1123/open-mifs/releases) 下载 `OpenMIFS.exe`，
双击运行，UAC 点「是」。程序会常驻任务栏通知区域。

> 未签名的 exe 首次运行可能被 SmartScreen 拦一下，点「更多信息 → 仍要运行」即可。
> 代码全部开源，`build/build.ps1` 可自行编译比对。

### 方式二：从源码直接跑

```powershell
git clone https://github.com/aie1123/open-mifs.git
cd open-mifs
```

- 图形界面：双击 `mifs-gui.cmd`
- 命令行：`.\mifs.cmd status`

如果直接运行 `.ps1` 被执行策略拦住：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\src\mifs.ps1 status
```

### 方式三：自己编译 exe

```powershell
.\build\make-icon.ps1      # 生成图标（仓库里已带，可跳过）
.\build\build.ps1          # 编译，产物在 dist\OpenMIFS.exe
```

约 3 秒完成，产物约 50 KB。

---

## 构建流程

| 文件 | 作用 |
| :--- | :--- |
| `build/build.ps1` | 用系统自带 `csc.exe` 编译 `src/csharp/OpenMIFS.cs` → `dist/OpenMIFS.exe`，内嵌图标与 `requireAdministrator` 清单，输出大小与 SHA256 |
| `build/make-icon.ps1` | 程序化生成多尺寸图标 `assets/icon.ico`（16/24/32/48/64/128/256） |
| `.github/workflows/build.yml` | CI：脚本语法检查 → 生成图标 → 编译 → 上传产物；打 `v*` tag 时自动发 Release |

**为什么不用 .NET SDK / MSBuild？**
Windows 自带的 `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 就能编译，
产物是真正的单文件 exe，只依赖系统内置的 .NET Framework 4.8。
这样别人 clone 下来不用装几百 MB 的 SDK 就能出包，CI 也更快。

**构建的两个坑（已在脚本里处理）**

- C# 源码与 `.ps1` 必须带 **UTF-8 BOM**，否则 `csc` / Windows PowerShell 5.1 会按系统代码页解析，中文全乱。
  `build.ps1` 会检测并自动补 BOM；`.gitattributes` 固定了换行符。
- 该 `csc` 只支持 **C# 5**，所以源码里不能用字符串插值、`?.`、`nameof` 等新语法。

想本地换图标（自用，不入库）：

```powershell
.\build\build.ps1 -Icon "C:\path\to\your.ico"
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

## 项目结构

```
open-mifs/
├─ OpenMIFS.exe            ← Releases 里的成品（不入库）
├─ mifs.cmd / mifs-gui.cmd ← 源码启动器，自动提权 + 绕过执行策略
├─ src/
│  ├─ mifs.ps1             ← 命令行版
│  ├─ mifs-gui.ps1         ← 图形界面（PowerShell + WinForms）
│  └─ csharp/
│     ├─ OpenMIFS.cs       ← 托盘 exe 源码
│     └─ app.manifest      ← requireAdministrator + DPI
├─ build/
│  ├─ build.ps1            ← 编译 exe
│  └─ make-icon.ps1        ← 生成图标
├─ assets/icon.ico         ← 原创图标
├─ docs/
│  ├─ PROTOCOL.md          ← 协议、功能号表、踩坑、被排除的方案
│  └─ TESTED-MODELS.md     ← 机型实测矩阵
└─ .github/                ← CI 与 issue 模板
```

---

## 常见问题

**Q：`probe` 说 WMI 类不存在？**
说明你的机型 BIOS 没有暴露 MIFS 接口，本工具不适用。

**Q：exe 双击没反应 / 被 SmartScreen 拦了？**
未签名 exe 的正常现象。点「更多信息 → 仍要运行」。确认运行后图标在任务栏右下角通知区域（可能要点小箭头展开）。

**Q：托盘图标不见了？**
Win11 默认会把新图标收进折叠区，拖到任务栏即可固定。

**Q：`probe` 说「无效的方法参数」？**
ACPI WMI 方法必须在**实例**上调用，不能在类上调用。本工具已处理；
如果你在自己写代码，记得用 `Invoke-CimMethod -InputObject <实例>`。

**Q：模式切换重启后还在吗？**
部分机型 EC 会保存，部分会被 BIOS 重置。重启后跑一次 `status` 即可确认。

**Q：切了模式感觉没变化？**
跑 `bench` 实测。它会给出各档的 CPU 性能百分比和风扇峰值，用数据说话。

**Q：会不会把电脑搞坏？**
本工具只使用 MIFS 接口公开的功能号，且都是官方定义的可逆开关，不写未知寄存器。
但请注意：**不要用 `raw` 对未知功能号发 SET**（脚本本身也不提供这个能力）。

---

## 关于图标

仓库里的 `assets/icon.ico` 是**原创设计**：圆角六边形 + 品牌紫→青渐变 + 三根递升档位柱。

刻意**没有**复用机械革命的商标图形（紫色六边形 + 白色闪电 S）。
那是对方的注册商标，放进公开 MIT 仓库既涉及商标与著作权风险，
也容易让人误以为这是官方出品（本项目与机械革命/同方无任何关联）。

想在本地自用官方图标，可以 `build\build.ps1 -Icon <你的.ico>`，该文件不会提交到仓库。

---

## 贡献

最有价值的贡献是**你的机型的实测数据**。请跑：

```powershell
.\mifs.cmd test
.\mifs.cmd probe
```

把完整输出贴进 issue（有专门的「机型实测数据」模板），并附上机型全称、CPU、BIOS 版本。
我会汇总进 [docs/TESTED-MODELS.md](docs/TESTED-MODELS.md)。

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
