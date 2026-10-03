# MIFS 接口协议说明

本文记录 OpenMIFS 依赖的接口、报文格式、功能号表，以及发现过程中的坑。
内容全部来自对公开 WMI 接口的观察 + 上游 Linux 内核驱动的公开信息，
不含任何厂商专有代码或逆向数据。

---

## 1. 接口位置

在 Windows 上，这个接口以标准 ACPI WMI 的形式暴露，不需要任何厂商驱动：

| 项目 | 值 |
| :--- | :--- |
| ACPI 设备 | `ACPI\PNP0C14\MIFS` |
| BIOS 设备名 | `\_SB.PCI0.WMID` |
| Windows WMI 类 | `root\wmi:MICommonInterface` |
| 类限定符 | `guid={b60bfb48-3e5b-49e4-a0e9-8cffe1b3434b}`，`provider=WmiProv` |
| 实例名 | `ACPI\PNP0C14\MIFS_0` |
| 方法 | `MiInterface`，`WmiMethodId=1` |

方法签名（来自类的 MOF 定义）：

```
MiInterface(
    [In , ID=0, MAX=32] uint8  InData[]     // 32 字节输入
    [Out, ID=1, MAX=30] uint8  OutData[]    // 30 字节输出
    [Out, ID=2]         uint16 Reserved
)
```

### 怎么确认自己的机器有没有

```powershell
Get-CimClass -Namespace root\wmi -ClassName MICommonInterface
```

有输出就说明 BIOS 暴露了 MIFS 接口。

### 确认 ACPI 里确实声明了它

`_WDG`（WMI Data Block GUID 列表）里能查到上面那个 GUID：

```powershell
# 从注册表缓存的 ACPI SSDT 里搜 GUID 的小端字节序
$guid = [System.Guid]::Parse('B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B')
$pat  = -join ($guid.ToByteArray() | ForEach-Object { $_.ToString('X2') })
Get-ChildItem 'HKLM:\HARDWARE\ACPI' -Recurse | ForEach-Object {
    $v = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).PSObject.Properties |
         Where-Object { $_.Value -is [byte[]] } | Select-Object -First 1
    if ($v) {
        $hex = -join ($v.Value | ForEach-Object { $_.ToString('X2') })
        if ($hex.Contains($pat)) { "命中: $($_.PSPath)" }
    }
}
```

---

## 2. 报文格式

### 请求（32 字节）

| 偏移 | 值 | 说明 |
| :---: | :--- | :--- |
| 0 | `00` | 固定 |
| 1 | 类型 | `250` (0xFA) = GET 读<br>`251` (0xFB) = SET 写 |
| 2 | `00` | 固定 |
| 3 | 功能号 | 见下表 |
| 4..31 | 参数 | 按功能号解释，通常只用前 1~3 字节 |

### 响应（30 字节，另有 2 字节 Reserved）

| 偏移 | 值 | 说明 |
| :---: | :--- | :--- |
| 0 | `00` | 固定 |
| 1 | `80` | 成功标志（每次成功调用都是 0x80） |
| 2 | `00` | 固定 |
| 3 | 功能号 | **回显请求里的功能号**，可用来校验报文没错位 |
| 4..29 | 数据 | 按功能号解释 |

**调用失败时**（功能号未实现）WMI 会直接抛异常，错误文本是 `常规故障`（Generic failure），
不会有任何响应数据。所以「抛异常」= 该功能号本机未实现。

但有例外：**有些功能号调用成功、`[1]` 也返回 `0x80`，数据却恒为 0** ——
这类同样说明本机 BIOS 没实现该功能（比如 `22` CPU 温度）。

---

## 3. 功能号表

功能号来自上游 Linux 内核驱动
[`tongfang-mifs-wmi`](https://lore-kernel.gnuweeb.org/platform-driver-x86/20260124124413.46017-1-qby140326@gmail.com/T/)
（作者 Mingyou Chen，GPL-2.0，2026-01 提交）。

| 功能号 | 名称 | 读写 | 数据位置 | 说明 |
| :---: | :--- | :---: | :--- | :--- |
| 8 | SYSTEM_PER_MODE | R/W | `[4]` | 性能模式：`0` 均衡 / `1` 性能 / `2` 低功耗 / `3` 满速（**注意机型差异，见 §5**） |
| 9 | GPU_MODE | R/W | `[4]` | `0` 混合 / `1` 独显直连 / `2` 核显 |
| 10 | KBD_TYPE | R | `[4]` | 键盘类型 |
| 11 | FN_LOCK | R/W | `[4]` | Fn 锁：`0` 关 / `1` 开 |
| 12 | TP_LOCK | R/W | `[4]` | 触控板锁：`0` 正常 / `1` 锁定 |
| 13 | FAN_SPEEDS | R | `[4..5]` `[6..7]` `[10..11]` | 三个风扇转速，**小端 16 位**（RPM） |
| 16 | RGB_KB_MODE | R/W | `[4]` | `0` 关 / `1` 循环 / `2` 固定 / `3` 自定义 |
| 17 | RGB_KB_COLOR | W | `[4..6]` | R、G、B 三字节 |
| 18 | RGB_KB_BRIGHTNESS | R/W | `[4]` | 键盘背光亮度 |
| 19 | SYSTEM_AC_TYPE | R | `[4]` | `1` 外接电源 / `0` 电池 |
| 20 | MAX_FAN_SWITCH | R/W | `[4][5]` | 风扇满速：参数 `[4]`=风扇组（0=CPU/GPU），`[5]`=0 正常 / 1 满速 |
| 21 | MAX_FAN_SPEED | R | `[4]` | 风扇满速值 |
| 22 | CPU_THERMOMETER | R | `[4]` | CPU 温度（℃） |
| 23 | CPU_POWER | R | `[4]` | CPU 功率 |

转速解析示例（`FAN_SPEEDS`，响应 `... C2 09 0A 0A 00 00 00 00 ...`）：

```
风扇1 = [5]<<8 | [4] = 0x09C2 = 2498 RPM
风扇2 = [7]<<8 | [6] = 0x0A0A = 2570 RPM
风扇3 = [11]<<8 | [10]
```

---

## 4. 两个必踩的坑

### 坑一：ACPI WMI 方法必须在**实例**上调用

这是本项目和上游驱动对不上号时的头号原因。

```powershell
# ❌ 错：在类上调用 —— 报「无效的方法参数」
Invoke-CimMethod -Namespace root\wmi -ClassName MICommonInterface `
    -MethodName MiInterface -Arguments @{ InData = $buf }

# ✅ 对：先取实例，再在实例上调用
$inst = Get-CimInstance -Namespace root\wmi -ClassName MICommonInterface
Invoke-CimMethod -InputObject $inst -MethodName MiInterface -Arguments @{ InData = $buf }
```

这和 `WmiMonitorBrightnessMethods` 之类的 ACPI WMI 类是一样的规矩。
非管理员调用该实例会直接「拒绝访问」，所以**必须提权**。

### 坑二：PowerShell 变量名不区分大小写

写这段代码时真实踩过：

```powershell
$FN = @{ PER_MODE = 8; GPU_MODE = 9; ... }   # 功能号表
foreach ($k in $FN.Keys) {
    $fn = $FN[$k]      # ❌ $fn 和 $FN 是同一个变量！
    ...
}
```

`$fn = $FN[$k]` 会把功能号表本身覆盖成整数，下一轮循环就 `无法对 Null 数组进行索引`。
PowerShell 里 `$fn` 和 `$FN` 是**同一个变量**。用 `$FNUM` / `$num` 这类不会撞的名字。

---

## 5. 性能模式的值编码因机型而异

上游驱动标注：`0=均衡 1=性能 2=低功耗 3=满速`。

但**无界 14 Pro 2023（R7-7840HS）实测相反**：`0=性能 1=均衡 2=低功耗`。

判断办法：跑 `.\src\mifs.ps1 bench`，它会依次设置 0/1/2/3 并加满核负载，
记录 CPU 性能百分比和风扇峰值。**性能百分比最低的那档就是最低功耗档**。

结果写回 `src/mifs.ps1` 和 `src/mifs-gui.ps1` 顶部的 `$ModeLabel` / `$ModeValue`。

---

## 6. 明确没有的能力

### 电池充电阈值

MIFS 的 14 个功能号里**没有任何一个**涉及充电上限。
`scan 0..63` 也没有扫出隐藏的充电相关功能号。

所以在这类机型上，「限制充电到 80%」无论用官方控制中心、本工具还是其他第三方工具
**都做不到**。可行的只有：

- 进 BIOS 找有没有相关选项（部分 INSYDE BIOS 有）
- 手动：充满就拔适配器，用到 60~70% 再插

### 风扇转速调节

`13` 号是**只读**的转速上报。

上游 Linux 驱动能调转速，是因为它另有**直接读写 EC RAM** 的通道：

| EC 地址 | 用途 |
| :--- | :--- |
| `0x0f00-0x0f2f` | CPU 自定义风扇曲线表（起止温度 + 转速） |
| `0x0f30-0x0f5f` | GPU 自定义风扇曲线表 |
| `0x1804` / `0x1809` | 直写两个风扇转速 |
| `0x07c5` / `0x07c6` | 启用自定义风扇表 |
| `0x078e` | 风扇控制能力位（bit6 = 支持统一风扇控制） |

这些**都不在 MIFS WMI 接口里**，Windows 侧没有安全的访问通道，
需要内核驱动才能碰。所以 OpenMIFS 不做风扇调速 —— 不是懒，是没路。

---

## 7. 其他被排除的路线

排查时验证过、但在这台机器上走不通的方案，记下来免得后来人重走：

| 方案 | 结论 |
| :--- | :--- |
| 上游 `tuxedo-drivers` / `mechrevo-drivers-dkms` | ❌ 该驱动要求机器暴露 6 个 Uniwill GUID（`ABBC0F6D`~`ABBC0F72`）。本机 ACPI 表（DSDT + 全部 SSDT）里逐字节搜过，**一个都没有**。本机走的是新的 MIFS 接口，不是老的 Uniwill 接口 |
| 官方控制中心 | ❌ 机械革命**从未**为无界 14 Pro 发布过控制台。官方驱动页只有 18 个基础驱动，没有控制台也没有 BIOS |
| 第三方 GUI（open-revo 等） | ❌ 依赖官方控制中心的内核驱动 `\\.\ACPIDriver`，没有官方控制台就只能跑演示模式；且面向 40/50 系游戏本 |

---

## 8. 在新型号上重新发现

换一台机器时，按这个顺序来：

```powershell
# 1. 接口在不在
.\mifs.cmd probe

# 2. 驱动清单里的功能号哪些响应
.\mifs.cmd test

# 3. 有没有清单外的隐藏功能号（只读，安全）
.\mifs.cmd scan 127

# 4. 校正性能模式映射
.\mifs.cmd bench
```

`scan` 会对 0..N 每个功能号发一次 GET。出现「数据不为全零」或「虽然响应但不在清单里」
的都值得记录，欢迎提 issue。

---

## 参考

- Linux 内核驱动提交：[platform/x86: tongfang-mifs-wmi: Add new Tongfang MIFS WMI driver](https://lore-kernel.gnuweeb.org/platform-driver-x86/20260124124413.46017-1-qby140326@gmail.com/T/)
- 相关上游驱动源码：[tuxedo-drivers](https://gitlab.com/tuxedocomputers/development/packages/tuxedo-drivers)（`uniwill_keyboard.c` / `uniwill_wmi.c` / `uniwill_interfaces.h`，其中的 EC 地址表很有参考价值）
- ACPI 规范：*Windows Management Instrumentation* 一节（`_WDG` / `_WD` / MOF 数据块）
