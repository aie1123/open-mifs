# OSD（Fn 屏幕提示）问题与处理

按 Fn 组合键时，笔记本通常会在屏幕上弹一个提示（音量、亮度、性能模式、Fn 锁……）。
本机（机械革命 无界 14 Pro 2023）的这套提示由厂商组件提供，**不显示**是常见故障。

本文记录这套组件是什么、怎么只读地诊断它、怎么安全地尝试修复，
以及 OpenMIFS 在官方 OSD 失效时的替代方案。

---

## 1. 厂商 OSD 是什么

安装位置：`C:\Program Files\OSD\`

| 文件 | 作用 |
| :--- | :--- |
| `BLDHotKeyService.exe` | Windows 服务 `BLDHotKeyService`（`Automatic` 启动）。负责接收/轮询 Fn 事件，并通知界面进程显示提示 |
| `BLDFnHotkeyUtility.exe` | OSD 界面本体，登录后运行在用户会话里，负责把提示画到屏幕上 |
| `InstallService.bat` / `UninstallService.bat` / `InstallUtil.exe` | 服务的安装/卸载脚本（.NET `InstallUtil` 方式注册服务） |
| `BLDHotKeyService.InstallLog` / `InstallUtil.InstallLog` | 服务安装日志（每次重装都会重写） |

官方驱动页对应机型 SKU（`WJ14Pro7840HS`）下能下载到 OSD 包 `OSD_V06.zip`：

```
https://driver.mechrevo.com/d.mechrevo.com/driver/MECHREVO2023/WJ14Pro7840HS/
```

> 这套组件与 OpenMIFS 使用的 MIFS WMI 接口**没有关系**：
> OSD 走自己的 EC 访问通道，OpenMIFS 走 `root\wmi:MICommonInterface`。
> 两者唯一的交集是「都能看到 Fn 键造成的同一批 EC 状态变化」。

---

## 2. 故障形态：组件没崩，但屏幕上什么都没有

这类故障最常见的形态是**服务、进程、事件投递全都正常，只有"画出来"这一步不动**。
所以要按下面四条依次取证，而不是看到"服务在跑"就下结论：

| 观察项 | 怎么拿 | 说明 |
| :--- | :--- | :--- |
| 服务状态 | `Get-Service BLDHotKeyService` / `.mifs.ps1 osd status` | `Running` 只说明进程活着 |
| 界面进程 | `Win32_Process` 里的 `BLDFnHotkeyUtility.exe` + 启动时间 | 有 PID 只说明进程没退出 |
| **事件投递** | Application 日志源 `OSDEvents` 的条数与最新时间 | **最关键**：有近期条目 = Fn 事件确实送到了 OSD 进程 |
| 服务心跳 | Application 日志源 `BLDHotKeyServiceEvent` 的 `...is running...` | 每 5 秒一条；停写说明服务这侧也断了 |

判读逻辑：

| 事件投递 | 心跳 | 结论 |
| :---: | :---: | :--- |
| ✅ 有新条目 | ✅ 仍在写 | **接收与触发都正常，坏在"画出来"**（DPI / 分层窗口 / 合成 / 覆盖层） |
| ✅ 有新条目 | ❌ 停写很久 | 服务侧链路断了，先重启服务 |
| ❌ 没有条目 | 任意 | Fn 事件没送到 OSD 进程（EC / 热键驱动层面），或该机型不用这个日志源 |
| 组件不存在 | — | 官方 OSD 包没装（可从驱动页下载 `OSD_V06.zip`） |

> ⚠️ **取证陷阱**：用 `EventLogReader` 正序读事件再截断，只会拿到窗口内最早的那批事件 ——
> 海量心跳（每 5 秒一条）会把扫描额度吃光，于是得出"心跳早就停了"的错误结论。
> 必须用 `EventLogQuery.ReverseDirection = true`（最新优先）读。
> OpenMIFS 的诊断器就是这么做的，并把心跳折叠成一行时间范围。

`OpenMIFS.exe --diagnose` 会把上面四条一次性写进
`%LOCALAPPDATA%\OpenMIFS\osd-diagnose.txt`。

---

## 3. 诊断：先取证，再动手

### 命令行

```powershell
.\src\mifs.ps1 osd status      # 服务/进程/启动时间/安装目录
.\src\mifs.ps1 osd diagnose    # 加上安装日志尾部与显示环境
OpenMIFS.exe --diagnose        # 完整证据：再加事件日志与显示器拓扑 → osd-diagnose.txt
```

exe 主界面「启动与 OSD → 诊断 OSD」按钮做的是同一件事。

### 证据里重点看什么

1. **`OSDEvents` 有没有近期条目**（`== OSD 事件投递 ==` 那一段）。
   有 = Fn 事件确实送到了 OSD 进程，接收环节正常 → 问题在显示环节。
2. **服务心跳最新一条距今多久**。心跳正常时每 5 秒一条。
3. **系统 DPI 与显示环境**。界面进程不感知 DPI，若系统缩放不是 100%，
   DPI 就是头号嫌疑；多显示器 / 缩放异常时还要考虑画到可视区外。
4. **安装日志尾部**有没有报错（`安装阶段已成功完成` = 服务注册正常）。
5. **`osd-diagnose.txt` 里有没有崩溃记录**（`.NET Runtime`、`Application Error`、`Windows Error Reporting`）。
   有崩溃 = 进程级问题；没有崩溃 + 事件投递正常 = **静默失效**，优先查 DPI / 分层窗口 / 第三方覆盖层。

---

## 4. 修复：按风险从低到高

### 第 0 步：先做对照实验（不花钱、最省事）

把系统缩放临时改成 **100%**（设置 → 系统 → 屏幕 → 缩放），注销或重启后按一次 Fn。
提示出现了 → 基本可以确认是 DPI 相关问题，用下面的「DPI 兼容修复」固化。
用眼睛看结果，**不要**用 `OSDEvents` 判断成功 —— 它坏着也照样写日志。

### 第 1 步：DPI 兼容修复（写一条可撤销的注册表值）

exe 主界面「启动与 OSD → DPI 兼容修复（实验，可撤销）」：

```
HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers
    C:\Program Files\OSD\BLDFnHotkeyUtility.exe  =  "~ HIGHDPIAWARE"
```

这就是「右键 exe → 属性 → 兼容性 → 更改高 DPI 设置」写的同一条项。**取消勾选即删除该值**，无残留。
写完需要重启 OSD 才生效（点「重启 OSD」按钮，或重新登录）。

### 第 2 步：重启服务与界面进程（可逆）

```powershell
.\src\mifs.ps1 osd restart
```

依次执行：

```
sc stop  BLDHotKeyService
（等服务真正停下，最多等 12 秒）
sc start BLDHotKeyService
taskkill /IM BLDFnHotkeyUtility.exe /F
start   "C:\Program Files\OSD\BLDFnHotkeyUtility.exe"
```

**不改注册表、不动驱动、不碰 EC。** 观察：重启后按一次 Fn 组合键，看提示是否出现。

### 第 3 步：重装服务（官方脚本，仍然可逆）

```powershell
# 需要管理员。官方脚本就在安装目录里
cd 'C:\Program Files\OSD'
.\UninstallService.bat
.\InstallService.bat
```

这一步会重新注册服务并重建 EventLog 源（安装日志里能看到
`正在日志 Application 中创建 EventLog 源 BLDHotKeyService`）。

### 第 4 步：重装官方 OSD 包

重新下载 `OSD_V06.zip`（上面那条驱动页链接）覆盖安装，然后重启。
**这是恢复官方 OSD 最彻底的方式**，但会重置厂商组件的配置。

### 第 5 步：给服务配失败恢复（防止它悄悄死掉）

```powershell
sc failure BLDHotKeyService reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

出厂设置里这项是空的（`RESET_PERIOD=0`），配上之后服务异常退出会自动拉起。

### 第 6 步：排除第三方干扰（风险中等）

按嫌疑逐个临时退出再试：虚拟显示器（如 GameViewer 的 `ROOT\DISPLAY\0000`）、
显卡 overlay、灯效软件、杀软的 HIPS/自我保护（把 `C:\Program Files\OSD\` 加信任区）。

### 不要做的事

- ❌ 不要用第三方"控制中心"去接管 OSD（例如面向 40/50 系游戏本的 `open-revo`：
  它依赖官方控制中心的内核驱动 `\\.\ACPIDriver`，本机从没装过官方控制中心）
- ❌ 不要把不匹配机型的 OSD 包/控制台装进来（会连带 Fn 热键映射一起坏掉）
- ❌ 不要在没备份的情况下直接删 `C:\Program Files\OSD`

---

## 5. 替代方案：OpenMIFS 自带的屏幕提示

官方 OSD 修不好、或者修好了但你还是想要一个更可控的提示时：用 exe 主界面
「启动与 OSD → 屏幕提示」的三档开关。

原理：每 **1.5 秒**通过 MIFS 读四个 Fn 键会改动的值 ——
性能模式（`fn=8`）、Fn 锁（`11`）、触控板锁（`12`）、键盘背光（`18`）——
任何一个发生变化，就在屏幕下方弹一条半透明提示，1.6 秒后自动消失。

| 场景 | 表现 |
| :--- | :--- |
| 在 OpenMIFS 里点按钮 | 立刻弹提示（动作完成即显示） |
| 按 Fn 组合键 | 最迟 1.5 秒后弹提示 |

### 5.1 与官方 OSD 重复怎么办（0.3.3 起自动处理）

官方 OSD 修好之后，按 Fn 会出现**两条提示**（官方一条 + 自带一条）。
0.3.3 起默认走 **自动（不重复）**：

1. 检测到外部（Fn）改动后，**延迟 350 ms** —— 先让官方 OSD 把它那条画出来
2. 枚举顶层窗口，找 `BLDFnHotkeyUtility.exe` 画提示用的分层窗：
   标题 `FloatingNativeWindow`、当前 `WS_VISIBLE`、尺寸在合理范围（0 < 宽高 ≤ 800）
3. 找到 → 记一条日志 `官方 OSD 正在显示，跳过自带提示：…` 并放弃显示自带的
4. 没找到（说明官方 OSD 没画）→ 弹自带的

三档模式（存在 `%LOCALAPPDATA%\OpenMIFS\settings.txt` 的 `osd_hint`）：

| 值 | 含义 |
| :--- | :--- |
| `auto`（默认） | 官方 OSD 可见就让位 |
| `always` | 总是显示自带的 |
| `off` | 关闭自带提示 |

**为什么判据是"窗口可见"而不是"服务/进程健康"**：本机实测过这三者同时成立
（服务 `Running`、界面进程在跑、`OSDEvents` 持续收到 Fn 事件），屏幕上却什么都没有 ——
组件健康 ≠ 真的画出来了。只有窗口可见性反映实际结果。

这也顺便成了一个**官方 OSD 的探测器**：
按 Fn 后如果日志里出现"跳过自带提示"，说明官方 OSD 正在正常工作；如果没有，说明它的显示环节又坏了。

### 5.2 边界

只能提示 MIFS 能读到的四个状态。音量、亮度、Wi-Fi、投屏这些由 EC/系统直接处理的提示，
MIFS 里没有对应功能号，OpenMIFS 无法替代（`scan 0..63` 也没扫出音量/亮度相关的隐藏功能号）。

---

## 6. 一句话总结

**厂商 OSD 走自己的 EC 通道，OpenMIFS 走 MIFS WMI；两者互不依赖。**

所以：官方 OSD 坏了不影响 OpenMIFS 控硬件；OpenMIFS 能只读地诊断它（服务/进程/心跳/事件日志），
能低风险地重启它（服务 + 界面进程），并在它失效时用 1.5 秒轮询的 EC 真值提示顶上。
