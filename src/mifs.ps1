<#
  OpenMIFS — 同方 MIFS (MiInterface) 命令行控制台
  ==================================================================
  不依赖任何官方控制中心组件，直接调用主板暴露的 MIFS WMI 接口。

  工作原理
    ACPI 设备      : ACPI\PNP0C14\MIFS
    Windows WMI 类 : root\wmi:MICommonInterface       （实例名 ACPI\PNP0C14\MIFS_0）
    方法           : MiInterface(InData[32]) -> OutData[30]
    ACPI GUID      : {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}
    报文           : InData[0]=00  InData[1]=类型  InData[2]=00  InData[3]=功能号  InData[4..]=参数
                     类型 250 = GET（读）   251 = SET（写）
    返回           : OutData[0]=00 [1]=80(成功标志) [2]=00 [3]=功能号回显 [4..]=数据
    功能号来源     : Linux 内核驱动 tongfang-mifs-wmi (GPL-2.0)，作者 Mingyou Chen
                     再由本机实测校正（见本文件顶部 $ModeLabel）

  用法
    .\mifs.ps1 status              显示状态（默认动作）
    .\mifs.ps1 sensors             传感器读数（封装功耗 / 频率 / 热区温度 / GPU / 内存 / 磁盘 / 风扇 / 电池）
    .\mifs.ps1 sensors probe       枚举本机可用的传感器计数器集与实例，逐项判定可用性、打印结论
    .\mifs.ps1 probe               诊断调用链（先跑这个）
    .\mifs.ps1 test                逐个探测驱动清单里的功能号
    .\mifs.ps1 scan [最大号]       扫描 0..N（默认 63）找出所有可用功能号
    .\mifs.ps1 bench               依次设 0/1/2/3 加满负载，实测各档功耗高低（约 2 分钟）
    .\mifs.ps1 mode low            切模式：low / balanced / performance（也接受 低功耗/均衡/性能）
    .\mifs.ps1 fanboost on         风扇满速开关（本机未实现，会提示）
    .\mifs.ps1 kbd 0               键盘背光 0~3
    .\mifs.ps1 raw 19              对指定功能号发 GET，打印原始字节
    .\mifs.ps1 osd status          OSD（Fn 屏幕提示）服务/进程状态
    .\mifs.ps1 osd restart         重启 OSD 服务与界面进程（修复 OSD 不显示的第一招）
    .\mifs.ps1 osd diagnose        OSD 诊断（服务/进程/安装日志/显示环境/事件日志）
    .\mifs.ps1 osd dpi             OSD DPI 兼容修复状态（本机 OSD 进程不感知 DPI）
    .\mifs.ps1 osd dpi-on          写入 DPI 兼容标记 ~ HIGHDPIAWARE（可撤销）
    .\mifs.ps1 osd dpi-off         撤销 DPI 兼容标记
    .\mifs.ps1 startup status      开机自启（计划任务 OpenMIFS）状态
    .\mifs.ps1 startup on|off      开关开机自启（计划任务 /RL HIGHEST + --tray，登录时静默进托盘、不弹窗）
    .\mifs.ps1 log                 打印日志路径并显示最后 20 行
    .\mifs.ps1 fan test            风扇满速实测：按转速判定（会先开再关，全程可逆）

  日志
    与图形版共用同一份日志：%LOCALAPPDATA%\OpenMIFS\openmifs.log（超过 1 MB 自动轮转）。

  安全
    status / sensors / sensors probe / probe / test / scan / raw
    osd status / osd diagnose / osd dpi / startup status / log
                                        只读，不改任何状态。
    mode / fanboost / kbd / osd restart / osd dpi-on|off / startup on|off
                                        会写 EC 寄存器、注册表或计划任务，均为可逆操作。
    不要在未确认含义的情况下对未知功能号发 SET。
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'probe', 'test', 'scan', 'bench', 'mode', 'fanboost', 'kbd', 'raw', 'osd', 'startup', 'log', 'sensors', 'fan')]
    [string]$Action = 'status',

    [Parameter(Position = 1)]
    [string]$Value
)

$ErrorActionPreference = 'Stop'

# ──────────────────────────────── 协议常量
$Namespace = 'root\wmi'
$ClassName = 'MICommonInterface'
$Method    = 'MiInterface'
$GET       = 250
$SET       = 251

# 驱动清单里的功能号
$FNUM = @{
    PER_MODE       = 8
    GPU_MODE       = 9
    KBD_TYPE       = 10
    FN_LOCK        = 11
    TP_LOCK        = 12
    FAN_SPEEDS     = 13
    RGB_MODE       = 16
    RGB_COLOR      = 17
    RGB_BRIGHT     = 18
    AC_TYPE        = 19
    MAX_FAN_SWITCH = 20
    MAX_FAN_SPEED  = 21
    CPU_TEMP       = 22
    CPU_POWER      = 23
}

# ──────────────────────────────── 性能模式映射【按需修改这里】
# 上游 Linux 驱动 tongfang-mifs-wmi 标注：0=均衡 1=性能 2=低功耗 3=满速
# 但本机（无界14 Pro 2023 / R7-7840HS / BIOS T140_PHX_V20）实测相反：
#     0 = 性能    1 = 均衡    2 = 低功耗    3 = 满速
# 换机型若发现对不上，运行  .\mifs.ps1 bench  用实测数据核对后改下面两行。
$ModeValue = [ordered]@{
    'performance' = 0; '性能' = 0
    'balanced'    = 1; '均衡' = 1
    'low'         = 2; '低功耗' = 2
}
$ModeLabel = @{ 0 = '性能'; 1 = '均衡'; 2 = '低功耗'; 3 = '满速' }

# ──────────────────────────────── 调用层
$script:Target = $null
$script:AttemptLog = New-Object System.Collections.Generic.List[string]

function Resolve-MifsTarget {
    $script:AttemptLog.Clear()
    try {
        $inst = Get-CimInstance -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop
        if ($inst) {
            $script:AttemptLog.Add("[1] Get-CimInstance 枚举 : OK，$( @($inst).Count ) 个实例")
            return [pscustomobject]@{ Kind = 'instance'; Obj = @($inst)[0]; How = 'Get-CimInstance' }
        }
        $script:AttemptLog.Add('[1] Get-CimInstance 枚举 : 0 个实例')
    }
    catch { $script:AttemptLog.Add("[1] Get-CimInstance 枚举 : $($_.Exception.Message)") }

    try {
        $mc = [wmiclass]"\\.\$Namespace`:$ClassName"
        $null = $mc.GetMethodParameters($Method)
        $script:AttemptLog.Add('[2] legacy DCOM        : OK')
        return [pscustomobject]@{ Kind = 'legacy'; Obj = $mc; How = 'legacy DCOM' }
    }
    catch { $script:AttemptLog.Add("[2] legacy DCOM        : $($_.Exception.Message)") }

    $script:AttemptLog.Add('[3] 兜底：类级调用')
    return [pscustomobject]@{ Kind = 'class'; Obj = $null; How = 'class-level(兜底)' }
}

function Invoke-Mifs {
    param(
        [Parameter(Mandatory)][int]$Type,
        [Parameter(Mandatory)][int]$Func,
        [byte[]]$SetPayload
    )
    if (-not $script:Target) { $script:Target = Resolve-MifsTarget }

    $buf = New-Object byte[] 32
    $buf[1] = [byte]$Type
    $buf[3] = [byte]$Func
    if ($SetPayload -and $SetPayload.Length -gt 0) {
        $n = [Math]::Min($SetPayload.Length, 28)
        [Array]::Copy($SetPayload, 0, $buf, 4, $n)
    }
    $argTable = @{ InData = $buf }
    $res = $null

    switch ($script:Target.Kind) {
        'instance' { $res = Invoke-CimMethod -InputObject $script:Target.Obj -MethodName $Method -Arguments $argTable }
        'legacy' {
            $inP = $script:Target.Obj.GetMethodParameters($Method)
            $inP['InData'] = $buf
            $res = $script:Target.Obj.InvokeMethod($Method, $inP, $null)
        }
        default { $res = Invoke-CimMethod -Namespace $Namespace -ClassName $ClassName -MethodName $Method -Arguments $argTable }
    }

    $out = if ($script:Target.Kind -eq 'legacy') { $res['OutData'] } else { $res.OutData }
    if (-not $out) { throw '方法返回空数据（OutData 为 null）' }
    return , ([byte[]]$out)
}

function Get-MifsByte {
    param([int]$Func)
    try { return (Invoke-Mifs -Type $GET -Func $Func)[4] } catch { return $null }
}

function Get-MifsFan {
    try {
        $f = Invoke-Mifs -Type $GET -Func $FNUM['FAN_SPEEDS']
        return @(
            ([int]$f[4] + ([int]$f[5] -shl 8)),
            ([int]$f[6] + ([int]$f[7] -shl 8)),
            ([int]$f[10] + ([int]$f[11] -shl 8))
        )
    }
    catch { return $null }
}

function Format-Bytes([byte[]]$bytes) {
    if (-not $bytes -or $bytes.Length -eq 0) { return '(空)' }
    return (($bytes | ForEach-Object { $_.ToString('X2') }) -join ' ')
}

function Set-MifsByte {
    param([int]$Func, [byte]$Val)
    $b = New-Object byte[] 1
    $b[0] = $Val
    Invoke-Mifs -Type $SET -Func $Func -SetPayload $b | Out-Null
}

# ──────────────────────────────── 日志（与图形版共用 %LOCALAPPDATA%\OpenMIFS\openmifs.log）
$script:LogDir  = Join-Path $env:LOCALAPPDATA 'OpenMIFS'
$script:LogFile = Join-Path $script:LogDir 'openmifs.log'

function Write-MifsLog {
    param([string]$Level, [string]$Message)
    try {
        if (-not (Test-Path $script:LogDir)) { New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null }
        $bom = New-Object System.Text.UTF8Encoding($true)
        $noBom = New-Object System.Text.UTF8Encoding($false)
        # 首次创建写 BOM，否则 PowerShell 5.1 / 记事本会把中文读成乱码
        if (-not (Test-Path $script:LogFile)) { [System.IO.File]::WriteAllText($script:LogFile, '', $bom) }
        if ((Get-Item $script:LogFile).Length -gt 1MB) {
            $old = "$($script:LogFile).1"
            if (Test-Path $old) { Remove-Item $old -Force }
            Move-Item $script:LogFile $old -Force
            [System.IO.File]::WriteAllText($script:LogFile, '', $bom)
        }
        $line = '{0} [{1}] pid={2} tid={3} | (CLI) {4}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'),
            $Level.PadRight(5), $PID, [System.Threading.Thread]::CurrentThread.ManagedThreadId, $Message
        [System.IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, $noBom)
    }
    catch { }
}

# ──────────────────────────────── OSD（Fn 屏幕提示）模块
$script:OsdService = 'BLDHotKeyService'
$script:OsdUtility = 'BLDFnHotkeyUtility.exe'
$script:OsdDir     = 'C:\Program Files\OSD'

# DPI 兼容标记：OSD 界面进程不感知 DPI，125%/150% 缩放下分层提示可能静默画不出来。
# 这条注册表项就是「属性 → 兼容性 → 更改高 DPI 设置」写的东西，删掉即还原。
$script:DpiKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
$script:DpiExe = Join-Path $script:OsdDir $script:OsdUtility
$script:DpiVal = '~ HIGHDPIAWARE'

function Get-OsdDpiFlag {
    try {
        $item = Get-ItemProperty -Path $script:DpiKey -ErrorAction SilentlyContinue
        if ($item) {
            $p = $item.PSObject.Properties[$script:DpiExe]
            if ($p) { return [string]$p.Value }
        }
    }
    catch { }
    return ''
}

function Test-OsdDpiEnabled { return ((Get-OsdDpiFlag) -match 'HIGHDPIAWARE') }

# 注意：powershell.exe 是 DPI 不感知进程，GetDpiForSystem / GetDpiForMonitor / Graphics.DpiX
# 在这种进程里一律返回 96，会把 125% 缩放误报成 100%。唯一可靠的办法是拿
# 「物理分辨率 ÷ 该进程看到的虚拟分辨率」这个比值。
function Get-SystemDpi {
    # ① 注册表里系统实际应用的 DPI，最准
    try {
        $a = (Get-ItemProperty 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name AppliedDPI -ErrorAction Stop).AppliedDPI
        if ($a -ge 96) { return [int]$a }
    }
    catch { }
    # ② 物理分辨率 ÷ 该进程看到的虚拟分辨率
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
        $virt = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
        $phys = (Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue |
                 Where-Object { $_.CurrentHorizontalResolution -gt 0 } |
                 Sort-Object CurrentHorizontalResolution -Descending |
                 Select-Object -First 1).CurrentHorizontalResolution
        if ($virt -gt 0 -and $phys -gt 0) {
            $pct = [math]::Round($phys / $virt * 100)
            if ($pct -ge 100 -and $pct -le 350) { return [int][math]::Round(96 * $pct / 100) }
        }
    }
    catch { }
    try {
        Add-Type -AssemblyName System.Drawing -ErrorAction SilentlyContinue
        return [int][System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero).DpiX
    }
    catch { return 0 }
}

# schtasks / sc / taskkill 会把错误写到 stderr；在 $ErrorActionPreference='Stop' 下
# PowerShell 5.1 会把原生命令的 stderr 当成终止性错误抛出。调用前临时改成 Continue。
function Invoke-NativeQuiet {
    param([string]$Exe, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & $Exe @Arguments 2>&1; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $prev }
    return [pscustomobject]@{ ExitCode = $code; Output = ($out | ForEach-Object { "$_" }) -join ' ' }
}

function Set-OsdDpiFlag {
    param([bool]$Enable)
    try {
        if (-not (Test-Path $script:DpiKey)) { New-Item -Path $script:DpiKey -Force | Out-Null }
        if ($Enable) {
            New-ItemProperty -Path $script:DpiKey -Name $script:DpiExe -Value $script:DpiVal -PropertyType String -Force | Out-Null
            Write-MifsLog 'INFO ' ("OSD DPI 兼容修复：写入 {0} = {1}" -f $script:DpiExe, $script:DpiVal)
            Write-Host ("已写入 DPI 兼容标记：{0} = {1}" -f $script:DpiExe, $script:DpiVal) -ForegroundColor Green
            Write-Host '下一步：.\mifs.ps1 osd restart，然后按一次 Fn 看提示是否出现。' -ForegroundColor Yellow
            Write-Host '撤销：.\mifs.ps1 osd dpi-off' -ForegroundColor DarkGray
        }
        else {
            Remove-ItemProperty -Path $script:DpiKey -Name $script:DpiExe -ErrorAction SilentlyContinue
            Write-MifsLog 'INFO ' ("OSD DPI 兼容修复：已删除 {0} 的标记" -f $script:DpiExe)
            Write-Host '已撤销 DPI 兼容标记（删除注册表值）。' -ForegroundColor Green
        }
    }
    catch {
        Write-MifsLog 'ERROR' ("设置 OSD DPI 兼容标记失败：{0}" -f $_.Exception.Message)
        Write-Host ("设置失败：{0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

function Get-OsdState {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$($script:OsdService)'" -ErrorAction SilentlyContinue
    $procs = @(Get-CimInstance Win32_Process -Filter "Name='$($script:OsdUtility)'" -ErrorAction SilentlyContinue)
    [pscustomobject]@{
        Found        = [bool]$svc
        ServiceState = if ($svc) { $svc.State } else { '未安装' }
        ServiceStart = if ($svc) { $svc.StartMode } else { '' }
        ServicePid   = if ($svc) { $svc.ProcessId } else { 0 }
        ServiceSince = if ($svc -and $svc.ProcessId) {
            try { (Get-CimInstance Win32_Process -Filter "ProcessId=$($svc.ProcessId)" -ErrorAction Stop).CreationDate } catch { $null }
        } else { $null }
        UtilityCount  = $procs.Count
        UtilityPids   = ($procs | ForEach-Object { $_.ProcessId }) -join ', '
        UtilitySince  = if ($procs.Count -gt 0) { $procs[0].CreationDate } else { $null }
    }
}

function Show-OsdStatus {
    Write-Host ''
    Write-Host '===== OSD（Fn 屏幕提示）状态 =====' -ForegroundColor Cyan
    $s = Get-OsdState
    $svcColor = if ($s.ServiceState -eq 'Running') { 'Green' } else { 'Red' }
    Write-Host ("服务          : {0} / {1}{2}" -f $script:OsdService, $s.ServiceState,
        $(if ($s.ServicePid) { "  (PID $($s.ServicePid)，启动于 $($s.ServiceSince))" } else { '' })) -ForegroundColor $svcColor
    Write-Host ("界面进程      : {0} 个{1}" -f $s.UtilityCount,
        $(if ($s.UtilityPids) { "  (PID $($s.UtilityPids)，启动于 $($s.UtilitySince))" } else { '' })) -ForegroundColor $(if ($s.UtilityCount -gt 0) { 'Green' } else { 'Red' })
    Write-Host ("安装目录      : {0}  {1}" -f $script:OsdDir, $(if (Test-Path $script:OsdDir) { '(存在)' } else { '(不存在！)' }))
    $dpiOn = Test-OsdDpiEnabled
    Write-Host ("DPI 兼容标记  : {0}" -f $(if ($dpiOn) { "已应用（$($script:DpiVal)）" } else { '未应用' })) -ForegroundColor $(if ($dpiOn) { 'Green' } else { 'DarkGray' })
    $dpi = Get-SystemDpi
    if ($dpi -gt 0) { Write-Host ("系统 DPI      : {0}（约 {1}% 缩放）" -f $dpi, [math]::Round($dpi / 96 * 100)) }
    Write-Host ''
    if ($s.ServiceState -eq 'Running' -and $s.UtilityCount -gt 0) {
        Write-Host '服务与进程都在跑。若屏幕上仍然看不到 OSD，按顺序试：' -ForegroundColor Yellow
        Write-Host '  .\mifs.ps1 osd diagnose     （先取证：Fn 事件到底有没有送到 OSD 进程）' -ForegroundColor Yellow
        Write-Host '  .\mifs.ps1 osd dpi-on       （写入 DPI 兼容标记，主要嫌疑）' -ForegroundColor Yellow
        Write-Host '  .\mifs.ps1 osd restart      （重启服务与界面进程让设置生效）' -ForegroundColor Yellow
    }
    Write-Host ''
}

function Invoke-OsdDiagnose {
    Write-Host ''
    Write-Host '===== OSD 诊断 =====' -ForegroundColor Cyan
    $s = Get-OsdState
    Write-Host '-- 服务 / 进程 --'
    Write-Host ("服务          : {0} / {1}  PID={2}  启动于 {3}" -f $script:OsdService, $s.ServiceState, $s.ServicePid, $s.ServiceSince)
    Write-Host ("界面进程      : {0} 个  PID={1}  启动于 {2}" -f $s.UtilityCount, $s.UtilityPids, $s.UtilitySince)
    Write-Host ''
    Write-Host '-- 安装目录 --'
    if (Test-Path $script:OsdDir) {
        Get-ChildItem $script:OsdDir | ForEach-Object { Write-Host ("  {0,-34} {1,10:N0} 字节  {2}" -f $_.Name, $_.Length, $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm')) }
    }
    else { Write-Host '  目录不存在' -ForegroundColor Red }
    Write-Host ''
    Write-Host '-- 服务安装日志尾部 --'
    $ilog = Join-Path $script:OsdDir 'BLDHotKeyService.InstallLog'
    if (Test-Path $ilog) {
        Get-Content $ilog -Tail 12 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    else { Write-Host '  没有 InstallLog' -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host '-- 显示环境 / DPI --'
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction SilentlyContinue
    try {
        [System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
            Write-Host ("  {0} {1}x{2} @ ({3},{4})" -f $(if ($_.Primary) { '主屏' } else { '副屏' }), $_.Bounds.Width, $_.Bounds.Height, $_.Bounds.Left, $_.Bounds.Top)
        }
    }
    catch { Write-Host '  查询失败' -ForegroundColor DarkGray }
    try {
        $dpi = Get-SystemDpi
        if ($dpi -gt 0) { Write-Host ("  系统 DPI    : {0}（约 {1}% 缩放）" -f $dpi, [math]::Round($dpi / 96 * 100)) }
    }
    catch { }
    Write-Host ("  DPI 兼容标记: {0}" -f $(if (Test-OsdDpiEnabled) { "已应用（$($script:DpiVal)）" } else { '未应用' }))
    Write-Host ''
    Write-Host '-- OSD 事件投递（OSDEvents，判断 Fn 事件有没有送到 OSD 进程）--'
    try {
        $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'OSDEvents' } -MaxEvents 5 -ErrorAction SilentlyContinue)
        if ($ev.Count -gt 0) {
            Write-Host ("  最新一条    : {0}   进程 {1}" -f $ev[0].TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'), $ev[0].Properties[0].Value) -ForegroundColor Green
            Write-Host ("  样例负载    : {0}" -f ($ev[0].Message -replace '\s+', ' '))
            Write-Host '  判读        : 有近期条目 = Fn 事件已送达，问题在「画出来」这一步（DPI/分层窗口）。'
        }
        else { Write-Host '  没有 OSDEvents 条目（或该机型不用这个日志源）—— 按一次 Fn 后再跑一次对比。' -ForegroundColor DarkGray }
    }
    catch { Write-Host '  查询 OSDEvents 失败（可能需要管理员）' -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host '-- 服务心跳（每 5 秒一条；停写说明服务这侧也断了）--'
    try {
        $hb = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'BLDHotKeyServiceEvent' } -MaxEvents 1 -ErrorAction SilentlyContinue)
        if ($hb.Count -gt 0) { Write-Host ("  最新一条    : {0}" -f $hb[0].TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')) }
        else { Write-Host '  没有心跳条目' -ForegroundColor DarkGray }
    }
    catch { Write-Host '  查询心跳失败' -ForegroundColor DarkGray }
    Write-Host ''
    Write-Host '完整证据（含折叠后的心跳时间范围）请用 exe 版：' -ForegroundColor Yellow
    Write-Host '  OpenMIFS.exe --diagnose     →  %LOCALAPPDATA%\OpenMIFS\osd-diagnose.txt' -ForegroundColor Yellow
    Write-Host ''
}

function Invoke-OsdRestart {
    Write-Host ''
    Write-Host '===== 重启 OSD =====' -ForegroundColor Cyan
    Write-Host ("  sc stop {0}" -f $script:OsdService)
    $o1 = Invoke-NativeQuiet -Exe 'sc.exe' -Arguments @('stop', $script:OsdService)
    Write-Host ("    exit={0} {1}" -f $o1.ExitCode, $o1.Output)
    # 必须等服务真正停稳再 start，否则会得到「服务正在停止」而启动失败
    for ($i = 0; $i -lt 24; $i++) {
        Start-Sleep -Milliseconds 500
        $st = (Get-CimInstance Win32_Service -Filter "Name='$($script:OsdService)'" -ErrorAction SilentlyContinue).State
        if ($st -and $st -notin 'Stopping', 'Running', 'Stop Pending') { break }
    }
    Write-Host ("  停止后状态: {0}" -f $st)
    Write-Host ("  sc start {0}" -f $script:OsdService)
    $o2 = Invoke-NativeQuiet -Exe 'sc.exe' -Arguments @('start', $script:OsdService)
    Write-Host ("    exit={0} {1}" -f $o2.ExitCode, $o2.Output)
    Write-Host ("  taskkill /IM {0} /F" -f $script:OsdUtility)
    $o3 = Invoke-NativeQuiet -Exe 'taskkill.exe' -Arguments @('/IM', $script:OsdUtility, '/F')
    Write-Host ("    exit={0} {1}" -f $o3.ExitCode, $o3.Output)
    Start-Sleep -Milliseconds 1200
    $exePath = Join-Path $script:OsdDir $script:OsdUtility
    if (Test-Path $exePath) {
        Start-Process -FilePath $exePath -WorkingDirectory $script:OsdDir
        Write-Host ("  已重新启动 {0}" -f $exePath) -ForegroundColor Green
    }
    else { Write-Host ("  未找到 {0}" -f $exePath) -ForegroundColor Red }
    Write-MifsLog 'INFO ' ("OSD 重启完成: stop={0} start={1} kill={2}" -f $o1.Output, $o2.Output, $o3.Output)
    Start-Sleep -Milliseconds 800
    Show-OsdStatus
}

# ──────────────────────────────── 开机自启（计划任务）
# 开机自启必须带 --tray：登录时只驻留托盘，不弹主界面。
# 老版本创建的任务没有这个参数（登录会弹窗），NeedsRepair 为真时提示重建。
function Get-StartupTask {
    $r = Invoke-NativeQuiet -Exe 'schtasks.exe' -Arguments @('/Query', '/TN', 'OpenMIFS')
    $enabled = ($r.ExitCode -eq 0)
    $hasTray = $false
    if ($enabled) {
        $x = Invoke-NativeQuiet -Exe 'schtasks.exe' -Arguments @('/Query', '/TN', 'OpenMIFS', '/XML')
        $hasTray = ($x.ExitCode -eq 0 -and $x.Output -match '--tray')
    }
    [pscustomobject]@{ Enabled = $enabled; HasTray = $hasTray; NeedsRepair = ($enabled -and -not $hasTray); Output = $r.Output }
}

function Set-StartupTask {
    param([bool]$Enable)
    $exePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\OpenMIFS.exe'
    if ($Enable -and -not (Test-Path $exePath)) {
        throw "找不到 $exePath —— 请先构建（build\build.ps1），或用 exe 版界面里的开关"
    }
    if ($Enable) {
        # /TR 里带 --tray：登录启动时静默进托盘（早期版本漏了这个参数，会弹主界面）
        $r = Invoke-NativeQuiet -Exe 'schtasks.exe' -Arguments @('/Create', '/TN', 'OpenMIFS', '/TR', "`"$exePath`" --tray",
                '/SC', 'ONLOGON', '/RL', 'HIGHEST', '/F', '/DELAY', '0000:10')
    }
    else {
        $r = Invoke-NativeQuiet -Exe 'schtasks.exe' -Arguments @('/Delete', '/TN', 'OpenMIFS', '/F')
    }
    $r.Output | ForEach-Object { Write-Host "  $_" }
    Write-MifsLog 'INFO ' ("开机自启 => {0}（exit={1}）{2}" -f $(if ($Enable) { 'on' } else { 'off' }), $r.ExitCode, $r.Output)
    if ($r.ExitCode -ne 0) { Write-Host ("设置失败（exit={0}）" -f $r.ExitCode) -ForegroundColor Red }
    else { Write-Host ("开机自启已{0}（计划任务 OpenMIFS，/RL HIGHEST，带 --tray 静默进托盘）" -f $(if ($Enable) { '启用' } else { '关闭' })) -ForegroundColor Green }
}

# ──────────────────────────────── 风扇实测（转速验证）
# 之前的判定用"寄存器读回值是否变化"，但 EC 可能不更新寄存器镜像却照做 ——
# 真正的判据是风扇转速：开 4~8 秒，看 RPM 有没有起来，最后恢复原状。
function Invoke-FanTest {
    Write-Host ''
    Write-Host '===== 风扇满速实测（按转速验证，不看寄存器读回）=====' -ForegroundColor Cyan
    $ac = Get-MifsByte -Func $FNUM['AC_TYPE']
    $acName = if ($null -eq $ac) { '读不到（需要管理员）' } else { switch ([int]$ac) { 0 { '电池供电' } 1 { 'Type-C 供电' } 2 { '圆口 DC 供电' } default { "原始值 $ac" } } }
    Write-Host ("当前供电   : {0}" -f $acName)

    $base = @()
    for ($i = 0; $i -lt 4; $i++) {
        $f = Get-MifsFan
        if ($f) { $base += [int]$f[0] }
        Start-Sleep -Milliseconds 700
    }
    if ($base.Count -eq 0) { Write-Host '读不到风扇转速，无法验证（需要管理员）' -ForegroundColor Red; return }
    $baseMax = ($base | Measure-Object -Maximum).Maximum
    Write-Host ("基线转速   : {0} RPM（4 次采样取最大）" -f $baseMax)

    $b = New-Object byte[] 2
    $b[0] = 0      # 风扇组 0 = CPU/GPU
    $b[1] = 1      # 状态 1 = 满速
    Write-Host '已发送 风扇满速=开，采样 8 秒…' -ForegroundColor Yellow
    Invoke-Mifs -Type $SET -Func $FNUM['MAX_FAN_SWITCH'] -SetPayload $b | Out-Null

    $after = @()
    for ($i = 0; $i -lt 8; $i++) {
        $f = Get-MifsFan
        if ($f) { $after += [int]$f[0] }
        Start-Sleep -Milliseconds 1000
    }
    $afterMax = ($after | Measure-Object -Maximum).Maximum
    $reg = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
    Write-Host ("加速后峰值 : {0} RPM     寄存器读回: {1}" -f $afterMax, $reg)

    $delta = $afterMax - $baseMax
    Write-Host ''
    if ($delta -ge 250) {
        Write-Host ("✅ 风扇确实加速了（+{0} RPM）" -f $delta) -ForegroundColor Green
        Write-Host '   结论：EC 只是不把状态写回寄存器（读回恒为 0），功能本身可用。' -ForegroundColor Green
        Write-Host '   OpenMIFS 已改为按转速判定，风扇满速按钮可用。' -ForegroundColor Green
    } else {
        Write-Host ("❌ 转速没有明显变化（{0} RPM）—— 该状态下 EC 不执行风扇满速" -f $delta) -ForegroundColor Yellow
        if ($null -ne $ac -and [int]$ac -eq 1) {
            Write-Host '   当前是 Type-C(PD) 供电；上游驱动文档说该状态下满速被硬件禁用。' -ForegroundColor Yellow
            Write-Host '   若本机没有圆口 DC 供电口，MIFS 这条路走不通，只能走 EC RAM（PawnIO 等），' -ForegroundColor Yellow
            Write-Host '   见 docs/FAN-CONTROL.md。' -ForegroundColor Yellow
        }
    }

    $b[1] = 0
    Invoke-Mifs -Type $SET -Func $FNUM['MAX_FAN_SWITCH'] -SetPayload $b | Out-Null
    Start-Sleep -Milliseconds 600
    $f2 = Get-MifsFan
    Write-Host ("已恢复 风扇满速=关{0}" -f $(if ($f2) { "（当前 $($f2[0]) RPM）" } else { '' })) -ForegroundColor Green
    Write-MifsLog 'INFO ' ("fan test: 供电={0} 基线={1} 峰值={2} 差值={3} 寄存器读回={4}" -f $acName, $baseMax, $afterMax, $delta, $reg)
    Write-Host ''
}

function Show-Log {
    Write-Host ''
    Write-Host '===== 日志 =====' -ForegroundColor Cyan
    Write-Host ("路径 : {0}" -f $script:LogFile)
    if (Test-Path $script:LogFile) {
        Write-Host ("大小 : {0:N0} 字节" -f (Get-Item $script:LogFile).Length)
        Write-Host ''
        Get-Content $script:LogFile -Encoding UTF8 -Tail 20 | ForEach-Object { Write-Host "  $_" }
    }
    else { Write-Host '（还没有日志文件 —— 运行任意动作后就会生成）' -ForegroundColor DarkGray }
    Write-Host ''
}

# ──────────────────────────────── 传感器（零驱动、只读；与 src/csharp/Sensors.cs 对齐）
# 数据源、单位与坑见 docs/SENSORS.md。三条硬规矩：
#   · PDH 一律用 Get-Counter 一次批量取，且必须 -SampleInterval 1 -MaxSamples 1
#     （它内部要采两次；速率类计数器 Power / % Processor Time 不预热就会读回 0 —— C# 版踩过）
#   · 实例名（rapl_package0_pkg / _tz.tz01）因机型而异 → 运行时探测，读不到就显示「未实现」，不猜
#   · 单位靠标定：Energy Meter 的 Power 是 mW；热区 Temperature 是 K，High Precision 是 0.1 K
$script:SensorSets  = @('Energy Meter', 'Thermal Zone Information', 'GPU Engine', 'GPU Adapter Memory', 'Processor Information')
$script:SensorError = ''
$script:SensorStats = @{ Ok = 0; Fail = 0 }

function Format-Inv {
    param([string]$Format, [object[]]$Arguments)
    return [string]::Format([System.Globalization.CultureInfo]::InvariantCulture, $Format, $Arguments)
}

# 中文按 2 列宽算，等宽面板才对得齐
function Get-DisplayWidth {
    param([string]$Text)
    $w = 0
    foreach ($ch in $Text.ToCharArray()) {
        $c = [int]$ch
        if (($c -ge 0x1100 -and $c -le 0x115F) -or ($c -ge 0x2E80 -and $c -le 0xA4CF) -or
            ($c -ge 0xAC00 -and $c -le 0xD7A3) -or ($c -ge 0xF900 -and $c -le 0xFAFF) -or
            ($c -ge 0xFE30 -and $c -le 0xFE6F) -or ($c -ge 0xFF00 -and $c -le 0xFF60) -or
            ($c -ge 0xFFE0 -and $c -le 0xFFE6)) { $w += 2 } else { $w += 1 }
    }
    return $w
}

function New-SensorReading {
    param([string]$Group, [string]$Name, [string]$Value, [bool]$Ok, [string]$Note = '')
    return [pscustomobject]@{ Group = $Group; Name = $Name; Value = $Value; Ok = $Ok; Note = $Note }
}

# 计数器集目录：只回答「本机有没有这个计数器/实例」，不采样，所以很快
function Get-CounterCatalog {
    param([string[]]$SetNames)
    $map = @{}
    foreach ($n in $SetNames) {
        try {
            $ls = Get-Counter -ListSet $n -ErrorAction Stop
            $inst = @(); $seen = @{}
            foreach ($p in @($ls.PathsWithInstances)) {
                # \Energy Meter(RAPL_Package0_PKG)\Power -> RAPL_Package0_PKG
                if ($p -match '^\\[^\\]+\((.+)\)\\[^\\]+$') {
                    if (-not $seen.ContainsKey($matches[1])) { $seen[$matches[1]] = $true; $inst += $matches[1] }
                }
            }
            $cnt = @(); $seenC = @{}
            foreach ($c in @($ls.Counter)) {
                $nm = $c
                if ($c -match '^\\[^\\]+\(\*\)\\(.+)$') { $nm = $matches[1] }
                if (-not $seenC.ContainsKey($nm)) { $seenC[$nm] = $true; $cnt += $nm }
            }
            $map[$n] = [pscustomobject]@{ Name = $n; Counters = $cnt; Instances = $inst }
        }
        catch { $map[$n] = $null }
    }
    return $map
}

function Test-CounterName {
    param($Catalog, [string]$SetName, [string]$CounterName)
    if (-not $Catalog.ContainsKey($SetName) -or -not $Catalog[$SetName]) { return $false }
    foreach ($c in $Catalog[$SetName].Counters) { if ($c -eq $CounterName) { return $true } }
    return $false
}

function Select-CounterInstance {
    param([string[]]$Instances, [string]$Pattern)
    $hit = @($Instances | Where-Object { $_ -match $Pattern }) | Sort-Object | Select-Object -First 1
    if ($null -eq $hit) { return '' }
    return [string]$hit
}

# 采样键：实例名 + 计数器名（小写）。
# 注意：Windows PowerShell 5.1 的 CounterSample 没有 CounterName 属性（读出来是空），
# 只能从 Path 尾部取计数器名 —— 两种 PowerShell 下都成立。
function Get-SampleKey {
    param($Sample)
    $p = [string]$Sample.Path
    $i = $p.LastIndexOf('\')
    $cn = $p
    if ($i -ge 0) { $cn = $p.Substring($i + 1) }
    return (([string]$Sample.InstanceName) + '|' + $cn).ToLowerInvariant()
}

# 一次批量取。批量失败（本机缺某个计数器会拖垮整批）才逐条降级，缺的那条只记错误
function Get-SensorSampleMap {
    param([string[]]$Paths)
    $map = @{}
    if (-not $Paths -or $Paths.Count -eq 0) { return $map }
    try {
        $set = Get-Counter -Counter $Paths -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
        foreach ($s in $set.CounterSamples) { $map[(Get-SampleKey $s)] = [double]$s.CookedValue }
        return $map
    }
    catch { }
    foreach ($p in $Paths) {
        try {
            $set = Get-Counter -Counter $p -SampleInterval 1 -MaxSamples 1 -ErrorAction Stop
            foreach ($s in $set.CounterSamples) { $map[(Get-SampleKey $s)] = [double]$s.CookedValue }
        }
        catch { $script:SensorError = "$p： $($_.Exception.Message)" }
    }
    return $map
}

function Get-SampleValue {
    param($Map, [string]$Instance, [string]$Counter)
    if (-not $Instance) { return $null }
    $k = ($Instance + '|' + $Counter).ToLowerInvariant()
    if ($Map.ContainsKey($k)) { return [double]$Map[$k] }
    return $null
}

function Get-MemoryTypeName {
    param([int]$Code)
    switch ($Code) {
        20 { return 'DDR' }
        21 { return 'DDR2' }
        24 { return 'DDR3' }
        26 { return 'DDR4' }
        27 { return 'LPDDR' }
        28 { return 'LPDDR2' }
        29 { return 'LPDDR3' }
        30 { return 'LPDDR4' }
        34 { return 'DDR5' }
        35 { return 'LPDDR5' }
    }
    if ($Code -gt 0) { return "类型$Code" }
    return ''
}

function Get-DiskMediaName {
    param([int]$Code)
    switch ($Code) {
        3 { return 'HDD' }
        4 { return 'SSD' }
        5 { return 'SCM' }
    }
    if ($Code -gt 0) { return "介质$Code" }
    return ''
}

function Get-DiskBusName {
    param([int]$Code)
    switch ($Code) {
        1 { return 'SCSI' }
        2 { return 'ATAPI' }
        3 { return 'ATA' }
        7 { return 'USB' }
        8 { return 'RAID' }
        10 { return 'SAS' }
        11 { return 'SATA' }
        13 { return 'MMC' }
        14 { return '虚拟' }
        15 { return '文件虚拟' }
        16 { return '存储空间' }
        17 { return 'NVMe' }
        18 { return 'SCM' }
        19 { return 'UFS' }
    }
    if ($Code -gt 0) { return "总线$Code" }
    return ''
}

function Get-DiskHealthName {
    param([int]$Code)
    switch ($Code) {
        0 { return '健康' }
        1 { return '警告' }
        2 { return '不健康' }
    }
    if ($Code -gt 0) { return "状态$Code" }
    return ''
}

# ── 读全部传感器（只读）
function Get-SensorReadings {
    $script:SensorError = ''
    $list = New-Object System.Collections.Generic.List[object]
    $catalog = Get-CounterCatalog -SetNames $script:SensorSets

    $emInst = @()
    if ($catalog['Energy Meter']) { $emInst = @($catalog['Energy Meter'].Instances) }
    $pkgInst  = Select-CounterInstance $emInst '^RAPL_Package\d+_PKG$'
    $sockInst = Select-CounterInstance $emInst '^Current Socket Power$'
    if (-not $sockInst) { $sockInst = Select-CounterInstance $emInst '^Apu Power$' }
    $vddInst  = Select-CounterInstance $emInst '^VDDCR_VDD Power$'
    $socInst  = Select-CounterInstance $emInst '^VDDCR_SOC Power$'
    $coreInst = @($emInst | Where-Object { $_ -match '^RAPL_Package\d+_Core\d+_CORE$' }) | Sort-Object

    $zoneInst = ''
    if ($catalog['Thermal Zone Information'] -and @($catalog['Thermal Zone Information'].Instances).Count -gt 0) {
        $zoneInst = [string]@($catalog['Thermal Zone Information'].Instances)[0]
    }

    # 含无效路径会让整批 Get-Counter 失败，所以先按目录过滤，再一次性采完
    $specs = @(
        @{ Set = 'Energy Meter';             Counter = 'Power';                      Path = '\Energy Meter(*)\Power' },
        @{ Set = 'Processor Information';    Counter = 'Processor Frequency';        Path = '\Processor Information(_Total)\Processor Frequency' },
        @{ Set = 'Processor Information';    Counter = '% Processor Performance';    Path = '\Processor Information(_Total)\% Processor Performance' },
        @{ Set = 'Processor Information';    Counter = '% Processor Time';           Path = '\Processor Information(_Total)\% Processor Time' },
        @{ Set = 'Thermal Zone Information'; Counter = 'Temperature';                Path = '\Thermal Zone Information(*)\Temperature' },
        @{ Set = 'Thermal Zone Information'; Counter = 'High Precision Temperature'; Path = '\Thermal Zone Information(*)\High Precision Temperature' },
        @{ Set = 'Thermal Zone Information'; Counter = 'Throttle Reasons';           Path = '\Thermal Zone Information(*)\Throttle Reasons' }
    )
    $paths = @()
    foreach ($sp in $specs) { if (Test-CounterName $catalog $sp.Set $sp.Counter) { $paths += $sp.Path } }
    $samples = Get-SensorSampleMap -Paths $paths

    # ── CPU：封装功耗
    $pkg = Get-SampleValue $samples $pkgInst 'Power'
    if ($null -ne $pkg) {
        $list.Add((New-SensorReading 'CPU' '封装功耗' (Format-Inv '{0:0.00} W' @($pkg / 1000.0)) $true ('PDH Energy Meter / ' + $pkgInst)))
    }
    else {
        $list.Add((New-SensorReading 'CPU' '封装功耗' '未实现' $false '本机没有 rapl_packageN_pkg 计数器'))
    }

    # ── CPU：有效频率（估算）= Processor Frequency × % Processor Performance
    $freq = Get-SampleValue $samples '_Total' 'Processor Frequency'
    $perf = Get-SampleValue $samples '_Total' '% Processor Performance'
    if ($null -ne $freq -and $null -ne $perf) {
        $list.Add((New-SensorReading 'CPU' '有效频率（估算）' (Format-Inv '{0:0.00} GHz' @($freq * $perf / 100.0 / 1000.0)) $true (Format-Inv '{0:0} MHz × {1:0.0}%' @($freq, $perf))))
    }
    else {
        $list.Add((New-SensorReading 'CPU' '有效频率' '未实现' $false 'Processor Information 计数器不可用'))
    }

    # ── CPU：负载
    $load = Get-SampleValue $samples '_Total' '% Processor Time'
    if ($null -ne $load) {
        $list.Add((New-SensorReading 'CPU' '负载' (Format-Inv '{0:0.0} %' @($load)) $true 'PDH % Processor Time（_Total）'))
    }

    # ── CPU：每核功耗（最多列 8 个）
    if (@($coreInst).Count -gt 0) {
        $sb = New-Object System.Text.StringBuilder
        $shown = 0
        foreach ($ci in @($coreInst)) {
            if ($shown -ge 8) { break }
            $v = Get-SampleValue $samples $ci 'Power'
            if ($null -eq $v) { continue }
            if ($shown -gt 0) { [void]$sb.Append(' / ') }
            [void]$sb.Append((Format-Inv '{0:0.00}' @($v / 1000.0)))
            $shown++
        }
        if ($shown -gt 0) {
            $list.Add((New-SensorReading 'CPU' '核心功耗 W' $sb.ToString() $true (Format-Inv '{0} 个核域（PDH）' @(@($coreInst).Count))))
        }
    }

    # ── CPU：供电域 VDDCR / SoC
    if ($vddInst -or $socInst) {
        $v1 = Get-SampleValue $samples $vddInst 'Power'
        $v2 = Get-SampleValue $samples $socInst 'Power'
        $t1 = if ($null -ne $v1) { Format-Inv '{0:0.00} W' @($v1 / 1000.0) } else { '—' }
        $t2 = if ($null -ne $v2) { Format-Inv '{0:0.00} W' @($v2 / 1000.0) } else { '—' }
        $list.Add((New-SensorReading 'CPU' 'VDDCR / SoC' ($t1 + ' / ' + $t2) ($null -ne $v1 -or $null -ne $v2) '供电域功耗（PDH Energy Meter）'))
    }

    # ── CPU：插槽功耗
    if ($sockInst) {
        $sock = Get-SampleValue $samples $sockInst 'Power'
        if ($null -ne $sock) {
            $list.Add((New-SensorReading 'CPU' '插槽功耗' (Format-Inv '{0:0.00} W' @($sock / 1000.0)) $true $sockInst))
        }
    }

    # ── 温度：ACPI 热区（只取第一个实例）
    if ($zoneInst) {
        $t  = Get-SampleValue $samples $zoneInst 'Temperature'
        $hp = Get-SampleValue $samples $zoneInst 'High Precision Temperature'
        $th = Get-SampleValue $samples $zoneInst 'Throttle Reasons'
        if ($null -ne $t) {
            $note = 'ACPI 热区 ' + $zoneInst
            if ($null -ne $hp) { $note += '，高精度 ' + (Format-Inv '{0:0.0} ℃' @($hp / 10.0 - 273.15)) }
            if ($null -ne $th) {
                $note += '，降频原因 ' + (Format-Inv '{0:0}' @($th))
                if ($th -eq 0) { $note += '（正常）' } else { $note += '（正在降频！）' }
            }
            $list.Add((New-SensorReading '温度' '热区 / 封装邻区' (Format-Inv '{0:0.0} ℃' @($t - 273.15)) $true $note))
        }
    }
    else {
        $list.Add((New-SensorReading '温度' '热区 / 封装邻区' '未实现' $false '本机没有 Thermal Zone Information 计数器'))
    }

    # ── 温度：CPU die 温度写死不支持
    $list.Add((New-SensorReading '温度' 'CPU die 温度' '不支持' $false 'AMD SMU 需要内核驱动，本项目不做（见 docs/SENSORS.md）'))

    # ── GPU：利用率（按 engtype_ 分组求和）
    $gpu = @()
    try { $gpu = @(Get-CimInstance -ClassName 'Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine' -ErrorAction Stop) }
    catch { $script:SensorError = "GPU Engine： $($_.Exception.Message)" }
    if (@($gpu).Count -gt 0) {
        $byType = @{}
        $total = 0.0
        foreach ($g in @($gpu)) {
            $val = [double]$g.UtilizationPercentage
            if ($val -le 0.01) { continue }
            $type = '其它'
            $k = ([string]$g.Name).IndexOf('engtype_', [System.StringComparison]::OrdinalIgnoreCase)
            if ($k -ge 0) { $type = ([string]$g.Name).Substring($k + 8) }
            if (-not $byType.ContainsKey($type)) { $byType[$type] = 0.0 }
            $byType[$type] += $val
            $total += $val
        }
        $busiest = '—'
        $best = 0.0
        foreach ($k in @($byType.Keys)) { if ($byType[$k] -gt $best) { $best = $byType[$k]; $busiest = $k } }
        $list.Add((New-SensorReading 'GPU' '利用率' (Format-Inv '{0} {1:0.0} %（合计 {2:0.0} %）' @($busiest, $best, $total)) $true 'WMI GPU Engine（Windows 原生）'))
    }
    else {
        $list.Add((New-SensorReading 'GPU' '利用率' '未实现' $false 'GPU Engine 计数器不可用'))
    }

    # ── GPU：专用显存占用
    $gm = @()
    try { $gm = @(Get-CimInstance -ClassName 'Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory' -ErrorAction Stop) }
    catch { }
    if (@($gm).Count -gt 0) {
        $memSum = 0.0
        foreach ($m in @($gm)) { if ([double]$m.DedicatedUsage -gt 0) { $memSum += [double]$m.DedicatedUsage } }
        $list.Add((New-SensorReading 'GPU' '专用显存占用' (Format-Inv '{0:0} MB' @($memSum / 1048576.0)) $true 'WMI GPU Adapter Memory'))
    }

    # ── GPU：温度 / 频率——不做 ADL P/Invoke（C# 版实测本机核显取不到）
    $list.Add((New-SensorReading 'GPU' '温度 / 频率' '未实现' $false '核显多不支持 ADL Overdrive 温度；与管理员权限无关，提权后同样取不到（C# 版实测）'))

    # ── 内存
    try {
        $mods = @(Get-CimInstance -ClassName 'Win32_PhysicalMemory' -ErrorAction Stop)
        $totalBytes = 0.0; $modules = 0; $speed = 0; $smbios = 0
        foreach ($m in $mods) {
            $modules++
            $totalBytes += [double]$m.Capacity
            $sp = [int]$m.ConfiguredClockSpeed
            if ($sp -eq 0) { $sp = [int]$m.Speed }
            if ($sp -gt $speed) { $speed = $sp }
            if ($smbios -eq 0) { $smbios = [int]$m.SMBIOSMemoryType }
        }
        $os = @(Get-CimInstance -ClassName 'Win32_OperatingSystem' -ErrorAction Stop)
        $totalKb = 0.0; $freeKb = 0.0
        if (@($os).Count -gt 0) {
            $totalKb = [double]$os[0].TotalVisibleMemorySize   # KB
            $freeKb  = [double]$os[0].FreePhysicalMemory       # KB
        }
        if ($modules -gt 0) {
            $typeName = Get-MemoryTypeName -Code $smbios
            $spec = Format-Inv '{0:0.0} GB  {1}×{2:0} GB {3}{4}' @(($totalBytes / 1073741824.0), $modules, ($totalBytes / 1073741824.0 / $modules), $typeName, $(if ($speed -gt 0) { '-' + $speed } else { '' }))
            $list.Add((New-SensorReading '内存' '容量 / 规格' $spec $true 'WMI Win32_PhysicalMemory'))
        }
        if ($totalKb -gt 0) {
            $usedGb = ($totalKb - $freeKb) / 1048576.0
            $totalGb = $totalKb / 1048576.0
            $list.Add((New-SensorReading '内存' '占用' (Format-Inv '{0:0.0} / {1:0.0} GB（{2:0.0} %）' @($usedGb, $totalGb, ($usedGb / $totalGb * 100.0))) $true 'WMI Win32_OperatingSystem'))
        }
    }
    catch { $script:SensorError = "读内存失败： $($_.Exception.Message)" }

    # ── 存储（可靠性计数器要管理员：整表查一次，再按 DeviceId 与磁盘配对）
    try {
        $rel = @{}
        $relError = ''
        try {
            foreach ($c in @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_StorageReliabilityCounter' -ErrorAction Stop)) {
                $did = [string]$c.DeviceId
                if ($did -and -not $rel.ContainsKey($did)) { $rel[$did] = $c }
            }
        }
        catch { $relError = $_.Exception.Message }

        foreach ($d in @(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName 'MSFT_PhysicalDisk' -ErrorAction Stop)) {
            $name  = [string]$d.FriendlyName
            $media = Get-DiskMediaName -Code ([int]$d.MediaType)
            $bus   = Get-DiskBusName -Code ([int]$d.BusType)
            $hea   = Get-DiskHealthName -Code ([int]$d.HealthStatus)
            $value = Format-Inv '{0:0} GB' @([double]$d.Size / 1073741824.0)
            $note  = 'WMI Storage'
            if ($bus) { $note += ' / ' + $bus }
            if ($hea) { $note += ' / ' + $hea }
            $did = [string]$d.DeviceId
            if ($rel.ContainsKey($did)) {
                $c = $rel[$did]
                $temp = [int]$c.Temperature
                $wear = [int]$c.Wear
                $hours = [long]$c.PowerOnHours
                if ($temp -gt 0) { $value += '  ' + $temp + ' ℃' } else { $value += '  温度未知' }
                $value += '  磨损 ' + $wear
                if ($hours -gt 0) { $value += '  通电 ' + $hours + ' h' }
                $note += ' + StorageReliabilityCounter'
            }
            elseif ($relError) { $note += '（温度需管理员：' + $relError + '）' }
            else { $note += '（该盘没有可靠性计数器）' }
            $title = $name
            if ($media) { $title += '（' + $media + '）' }
            $list.Add((New-SensorReading '存储' $title $value $true $note))
        }
    }
    catch { $script:SensorError = "读磁盘失败： $($_.Exception.Message)" }

    # ── 风扇（复用项目已有的 MIFS 读取）
    $fan = Get-MifsFan
    if ($fan) {
        $f3 = ''
        if ($fan[2] -gt 0) { $f3 = ' / ' + $fan[2] }
        $list.Add((New-SensorReading '风扇 / 电池' '风扇' (Format-Inv '{0} / {1}{2} RPM' @($fan[0], $fan[1], $f3)) $true 'MIFS fn=13'))
    }
    else {
        $list.Add((New-SensorReading '风扇 / 电池' '风扇' '未实现' $false 'MIFS 不可用（需要管理员）'))
    }

    # ── 电池
    try {
        $charge = $null; $status = $null
        $bat = @(Get-CimInstance -ClassName 'Win32_Battery' -ErrorAction Stop)
        if (@($bat).Count -gt 0) {
            $charge = [int]$bat[0].EstimatedChargeRemaining
            $status = [int]$bat[0].BatteryStatus      # 2 = 外接电源
        }
        # 必须投影属性：整实例枚举（不带 -Property）在本机会报「常规故障」，
        # 而 C# 的 WQL SELECT DesignedCapacity 是投影查询 —— 两者等价，投影才取得到值
        $design = 0; $full = 0
        try {
            $d = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'BatteryStaticData' -Property DesignedCapacity -ErrorAction Stop)
            if ($d.Count -gt 0) { $design = [int]$d[0].DesignedCapacity }
        }
        catch { }
        try {
            $f = @(Get-CimInstance -Namespace 'root/wmi' -ClassName 'BatteryFullChargedCapacity' -Property FullChargedCapacity -ErrorAction Stop)
            if ($f.Count -gt 0) { $full = [int]$f[0].FullChargedCapacity }
        }
        catch { }
        if ($null -ne $charge) {
            $ac = ''
            if ($null -ne $status) { if ($status -eq 2) { $ac = '外接电源' } else { $ac = '电池供电' } }
            $v = [string]$charge + ' %'
            if ($ac) { $v += '  ' + $ac }
            $note = 'WMI Win32_Battery'
            if ($design -gt 0 -and $full -gt 0) {
                $v += Format-Inv '  健康 {0:0.0} %（{1}/{2} mWh）' @((($full * 100.0) / $design), $full, $design)
                $note += ' + root\wmi 电池容量'
            }
            elseif ($full -gt 0) {
                $v += Format-Inv '  满充容量 {0} mWh' @($full)
                $note += ' + root\wmi 满充容量'
            }
            elseif ($design -gt 0) {
                $v += Format-Inv '  设计容量 {0} mWh' @($design)
                $note += ' + root\wmi 设计容量'
            }
            else {
                $v += '  电池容量需管理员'
            }
            $list.Add((New-SensorReading '风扇 / 电池' '电池' $v $true $note))
        }
    }
    catch { $script:SensorError = "读电池失败： $($_.Exception.Message)" }

    # 注意：PowerShell 5.1 里 @($genericList) 会抛「Argument types do not match」，用 .Count
    $okCount = @($list | Where-Object { $_.Ok }).Count
    $script:SensorStats = @{ Ok = $okCount; Fail = ($list.Count - $okCount) }
    return $list
}

# ── 渲染成等宽文本面板（GUI 直接用这个）
function Format-SensorPanel {
    param($Readings)
    $sb = New-Object System.Collections.Generic.List[string]
    $group = ''
    foreach ($r in $Readings) {
        if ($r.Group -ne $group) { $group = $r.Group; $sb.Add("== $group ==") }
        # 标签统一按 16 显示列对齐（最长的是「有效频率（估算）」= 8 汉字 = 16 列），
        # 动态名字（磁盘型号）超长时自然溢出，不让冒号错位
        $pad = 16 - (Get-DisplayWidth $r.Name)
        if ($pad -lt 0) { $pad = 0 }
        $sb.Add($r.Name + (' ' * $pad) + ': ' + $r.Value)
        if ($r.Note) { $sb.Add((' ' * 17) + '└ ' + $r.Note) }
    }
    $sb.Add('')
    $sb.Add('数据源：PDH(Energy Meter / Thermal Zone / GPU Engine / Processor) + WMI + MIFS')
    $sb.Add('刷新  ：' + (Get-Date).ToString('HH:mm:ss'))
    if ($script:SensorError) { $sb.Add('最后错误：' + $script:SensorError) }
    return ($sb -join "`r`n")
}

# ── 探测报告（纯文本行；CLI 上色打印，GUI 直接拼成字符串）
function Get-SensorProbeLines {
    $L = New-Object System.Collections.Generic.List[string]
    $L.Add('===== OpenMIFS 传感器探测 =====')
    $L.Add("主机：$env:COMPUTERNAME   时间：$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))")
    $L.Add('')
    $catalog = Get-CounterCatalog -SetNames $script:SensorSets
    foreach ($n in $script:SensorSets) {
        $L.Add("── 计数器集：$n")
        $c = $catalog[$n]
        if (-not $c) { $L.Add('   不可用（本机没有这个计数器集）'); $L.Add(''); continue }
        $L.Add('   计数器：' + (@($c.Counters) -join '、'))
        $inst = @($c.Instances)
        if ($inst.Count -eq 0) { $L.Add('   实例  ：（无实例）') }
        elseif ($inst.Count -le 10) { $L.Add('   实例  ：' + ($inst -join '、')) }
        else { $L.Add('   实例  ：' + (($inst | Select-Object -First 8) -join '、') + (Format-Inv ' …（共 {0} 个）' @($inst.Count))) }
        $L.Add('')
    }
    $L.Add('── 逐项探测')
    $readings = Get-SensorReadings
    $group = ''
    foreach ($r in $readings) {
        if ($r.Group -ne $group) { $group = $r.Group; $L.Add("   [$group]") }
        $tag = 'FAIL'
        if ($r.Ok) { $tag = 'OK  ' }
        $line = "   $tag $($r.Name) = $($r.Value)"
        if ($r.Note) { $line += "    ← $($r.Note)" }
        $L.Add($line)
    }
    $L.Add('')
    $L.Add('── 结论')
    $L.Add((Format-Inv '   可用 {0} 项 / 未实现 {1} 项' @($script:SensorStats.Ok, $script:SensorStats.Fail)))
    $L.Add('   本机可用的免驱动数据源已列在上方（OK 行）。FAIL 行说明该机型没有对应计数器/接口。')
    $L.Add('   CPU die 温度、主板/VRM/内存温度需要内核驱动，本项目刻意不做，见 docs/SENSORS.md。')
    return $L
}

function Write-SensorLog {
    param([string]$Action)
    Write-MifsLog 'INFO ' ("{0}: 可用 {1} 项 / 未实现 {2} 项" -f $Action, $script:SensorStats.Ok, $script:SensorStats.Fail)
}

function Show-Sensors {
    Write-Host ''
    Write-Host '===== OpenMIFS 传感器（零驱动，只读） =====' -ForegroundColor Cyan
    $readings = Get-SensorReadings
    $group = ''
    foreach ($r in $readings) {
        if ($r.Group -ne $group) { $group = $r.Group; Write-Host "== $group ==" -ForegroundColor Cyan }
        $pad = 16 - (Get-DisplayWidth $r.Name)
        if ($pad -lt 0) { $pad = 0 }
        $line = $r.Name + (' ' * $pad) + ': ' + $r.Value
        if ($r.Ok) { Write-Host $line } else { Write-Host $line -ForegroundColor DarkGray }
        if ($r.Note) { Write-Host ((' ' * 17) + '└ ' + $r.Note) -ForegroundColor DarkGray }
    }
    Write-Host ''
    Write-Host '数据源：PDH(Energy Meter / Thermal Zone / GPU Engine / Processor) + WMI + MIFS'
    Write-Host ("刷新  ：{0}" -f (Get-Date).ToString('HH:mm:ss'))
    if ($script:SensorError) { Write-Host ("最后错误：{0}" -f $script:SensorError) -ForegroundColor DarkGray }
    Write-Host ''
    Write-SensorLog 'sensors'
}

function Show-SensorsProbe {
    Write-Host ''
    $lines = Get-SensorProbeLines
    foreach ($ln in $lines) {
        if ($ln -like '=====*' -or $ln -like '──*') { Write-Host $ln -ForegroundColor Cyan }
        elseif ($ln -like '*FAIL*') { Write-Host $ln -ForegroundColor DarkGray }
        else { Write-Host $ln }
    }
    Write-Host ''
    Write-SensorLog 'sensors probe'
}

# ──────────────────────────────── 动作
function Show-Probe {
    Write-Host ''
    Write-Host '===== OpenMIFS 诊断 =====' -ForegroundColor Cyan
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host ("是否管理员 : {0}" -f $isAdmin)

    $cls = Get-CimClass -Namespace $Namespace -ClassName $ClassName -ErrorAction SilentlyContinue
    if ($cls) {
        $g = ($cls.CimClassQualifiers | Where-Object { $_.Name -eq 'GUID' }).Value
        Write-Host ("WMI 类     : {0}   GUID={1}" -f $cls.CimClassName, $g)
    }
    else {
        Write-Host 'WMI 类     : 不存在 —— 本机 BIOS 未暴露 MIFS 接口，本工具不适用' -ForegroundColor Red
        return
    }

    Write-Host ''
    Write-Host '调用链解析：'
    $script:Target = Resolve-MifsTarget
    $script:AttemptLog | ForEach-Object { Write-Host "  $_" }
    Write-Host ("选定策略   : {0}" -f $script:Target.How) -ForegroundColor Green

    if ($script:Target.Kind -eq 'instance') {
        Write-Host ''
        Write-Host '实例属性：'
        ($script:Target.Obj | Format-List * | Out-String).Trim() | Write-Host
    }

    Write-Host ''
    Write-Host '试发 GET(fn=8 PER_MODE)：' -ForegroundColor Cyan
    try {
        $o = Invoke-Mifs -Type $GET -Func $FNUM['PER_MODE']
        Write-Host ("  成功 -> {0}" -f (Format-Bytes $o)) -ForegroundColor Green
        $m = [int]$o[4]
        Write-Host ("  当前模式值 = {0}（{1}）" -f $m, $(if ($ModeLabel.ContainsKey($m)) { $ModeLabel[$m] } else { '未知' }))
    }
    catch {
        Write-Host ("  失败 -> {0}" -f $_.Exception.Message) -ForegroundColor Red
        ($_ | Out-String) | Write-Host
        ($_.Exception | Format-List * -Force | Out-String).Trim() | Write-Host
    }
    Write-Host ''
}

function Show-Test {
    Write-Host ''
    Write-Host '===== 功能号探测（全部为 GET，只读） =====' -ForegroundColor Cyan
    $ok = 0; $na = 0
    foreach ($name in ($FNUM.Keys | Sort-Object { $FNUM[$_] })) {
        $num = $FNUM[$name]
        try {
            $out = Invoke-Mifs -Type $GET -Func $num
            Write-Host ("  fn={0,-3} {1,-14} -> {2}" -f $num, $name, (Format-Bytes $out))
            $ok++
        }
        catch {
            Write-Host ("  fn={0,-3} {1,-14} -> 未实现（{2}）" -f $num, $name, $_.Exception.Message) -ForegroundColor DarkGray
            $na++
        }
    }
    Write-Host ''
    Write-Host ("小结：响应 {0} 个 / 未实现 {1} 个" -f $ok, $na) -ForegroundColor Cyan
    Write-Host '注意：有的功能号调用成功但数据恒为 0，同样说明本机 BIOS 未实现该功能。' -ForegroundColor DarkGray
    Write-Host ''
}

function Invoke-Scan {
    param([int]$Limit = 63)
    Write-Host ''
    Write-Host ("===== 功能号扫描 0..{0}（全部为 GET，只读） =====" -f $Limit) -ForegroundColor Cyan
    $known = @{}
    foreach ($k in $FNUM.Keys) { $known[[int]$FNUM[$k]] = $k }
    $hit = 0
    for ($f = 0; $f -le $Limit; $f++) {
        try {
            $o = Invoke-Mifs -Type $GET -Func $f
            $data = (($o[4..11]) | ForEach-Object { $_.ToString('X2') }) -join ' '
            $nm = if ($known.ContainsKey($f)) { $known[$f] } else { '(不在驱动清单)' }
            $mark = if ($known.ContainsKey($f)) { ' ' } else { '*' }
            Write-Host ("{0} fn={1,-3} {2,-16} 数据[4..11]= {3}" -f $mark, $f, $nm, $data)
            $hit++
        }
        catch { }
    }
    Write-Host ''
    Write-Host ("响应 {0} 个功能号；带 * 的是驱动清单外新发现的" -f $hit) -ForegroundColor Cyan
    Write-Host ''
}

function Invoke-Bench {
    Write-Host ''
    Write-Host '===== 模式实测（约 2 分钟，风扇会明显转起来） =====' -ForegroundColor Cyan
    Write-Host '依次把性能模式设为 0/1/2/3，每档加约 16 秒满核负载，记录 CPU 性能% 与风扇峰值。' -ForegroundColor DarkGray
    Write-Host ''
    $cores = [Environment]::ProcessorCount
    $rows = @()
    foreach ($v in 0, 1, 2, 3) {
        Write-Host ("  [值 {0}] 设置并加载中…" -f $v) -ForegroundColor Yellow
        try { Set-MifsByte -Func $FNUM['PER_MODE'] -Val ([byte]$v) }
        catch { Write-Host ("    无法设置：{0}" -f $_.Exception.Message) -ForegroundColor DarkGray; continue }
        Start-Sleep -Seconds 2

        $workers = New-Object System.Collections.ArrayList
        for ($i = 0; $i -lt $cores; $i++) {
            $ps = [powershell]::Create()
            $null = $ps.AddScript('$end=(Get-Date).AddSeconds(16); $x=0.0; while((Get-Date) -lt $end){ for($j=0;$j -lt 200000;$j++){ $x=[Math]::Sqrt($j) } }')
            $null = $ps.BeginInvoke()
            [void]$workers.Add($ps)
        }

        $perf = @()
        $fanPeak = 0
        for ($t = 0; $t -lt 8; $t++) {
            Start-Sleep -Seconds 2
            try {
                $s = (Get-Counter '\Processor Information(_Total)\% Processor Performance' -ErrorAction Stop).CounterSamples[0].CookedValue
                $perf += [double]$s
            }
            catch { }
            $fan = Get-MifsFan
            if ($fan -and [int]$fan[0] -gt $fanPeak) { $fanPeak = [int]$fan[0] }
        }
        foreach ($w in $workers) { try { $w.Stop() } catch { }; try { $w.Dispose() } catch { } }

        $med = 0
        if ($perf.Count -gt 0) { $med = ($perf | Sort-Object)[[int](($perf.Count - 1) / 2)] }
        $rows += [pscustomobject]@{
            值       = $v
            标签     = $ModeLabel[[int]$v]
            性能百分比 = [math]::Round($med, 1)
            风扇峰值 = $fanPeak
        }
        Start-Sleep -Seconds 5
    }

    Write-Host ''
    if ($rows.Count -eq 0) { Write-Host '没有成功设置的档位。' -ForegroundColor Red; return }
    ($rows | Format-Table -AutoSize | Out-String -Width 120).Trim() | Write-Host

    $sorted = $rows | Sort-Object 性能百分比
    Write-Host ''
    Write-Host '按功耗墙从低到高（性能百分比越低 = 功耗墙越低）：' -ForegroundColor Cyan
    foreach ($r in $sorted) {
        Write-Host ("  值 {0} -> 实测 {1,-6}  性能% {2,-6}  风扇峰值 {3} RPM" -f $r.值, $r.标签, $r.性能百分比, $r.风扇峰值)
    }
    Write-Host ''
    Write-Host ("  最低功耗 = 值 {0}    最高性能 = 值 {1}" -f $sorted[0].值, $sorted[-1].值) -ForegroundColor Green
    Write-Host '  若与脚本顶部 $ModeLabel 不符，直接改那几行。' -ForegroundColor DarkGray
    Write-Host ''
}

function Show-Status {
    Write-Host ''
    Write-Host '===== OpenMIFS 状态 =====' -ForegroundColor Cyan

    $pm = Get-MifsByte -Func $FNUM['PER_MODE']
    if ($null -ne $pm) {
        $m = [int]$pm
        Write-Host ("性能模式   : {0}" -f $(if ($ModeLabel.ContainsKey($m)) { $ModeLabel[$m] } else { "未知($m)" }))
    }
    else { Write-Host '性能模式   : 未实现' -ForegroundColor DarkGray }

    $fan = Get-MifsFan
    if ($fan) {
        Write-Host ("风扇转速   : 风扇1 {0} RPM / 风扇2 {1} RPM{2}" -f $fan[0], $fan[1], $(if ($fan[2] -gt 0) { " / 风扇3 $($fan[2]) RPM" } else { '' }))
    }
    else { Write-Host '风扇转速   : 未实现' -ForegroundColor DarkGray }

    $ac = Get-MifsByte -Func $FNUM['AC_TYPE']
    if ($null -ne $ac) {
        Write-Host ("供电状态   : {0}" -f $(switch ([int]$ac) { 0 { '电池供电' } 1 { 'Type-C 供电（风扇满速/满速模式被硬件禁用）' } 2 { '圆口 DC 供电' } default { "原始值 $ac" } }))
    }

    $kbd = Get-MifsByte -Func $FNUM['RGB_BRIGHT']
    if ($null -ne $kbd) { Write-Host ("键盘背光   : 等级 {0}" -f $kbd) }

    $fnl = Get-MifsByte -Func $FNUM['FN_LOCK']
    if ($null -ne $fnl) { Write-Host ("Fn 锁      : {0}" -f $(if ([int]$fnl -eq 1) { '开' } else { '关' })) }

    $tpl = Get-MifsByte -Func $FNUM['TP_LOCK']
    if ($null -ne $tpl) { Write-Host ("触控板锁   : {0}" -f $(if ([int]$tpl -eq 1) { '已锁定' } else { '正常' })) }

    $mxs = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
    if ($null -ne $mxs) { Write-Host ("风扇满速   : {0}" -f $(if ([int]$mxs -eq 1) { '开' } else { '关' })) }

    $ct = Get-MifsByte -Func $FNUM['CPU_TEMP']
    if ($null -ne $ct) { Write-Host ("CPU 温度   : {0}" -f $(if ([int]$ct -gt 0) { "$ct ℃" } else { '未实现' })) }

    try {
        $s = Get-OsdState
        $color = if ($s.ServiceState -eq 'Running' -and $s.UtilityCount -gt 0) { 'Gray' } else { 'Red' }
        Write-Host ("OSD        : 服务 {0} / 界面进程 {1} 个（详见 .\mifs.ps1 osd status）" -f $s.ServiceState, $s.UtilityCount) -ForegroundColor $color
    }
    catch { }
    try {
        $t = Get-StartupTask
        Write-Host ("开机自启   : {0}" -f $(if ($t.NeedsRepair) { '已启用，但不带 --tray（登录会弹窗）→ 跑 .\mifs.cmd startup on 重建' } elseif ($t.Enabled) { '已启用（计划任务 OpenMIFS --tray，静默进托盘）' } else { '未启用' }))
    }
    catch { }
    Write-Host ''
}

# ──────────────────────────────── 主流程
try {
    switch ($Action) {
        'status' { Show-Status }
        'sensors' {
            if ($Value -eq 'probe') { Show-SensorsProbe }
            elseif (-not $Value -or $Value -eq 'status') { Show-Sensors }
            else { throw 'sensors 需要 status / probe' }
        }
        'probe'  { Show-Probe }
        'test'   { Show-Test }
        'scan'   {
            $scanMax = 63
            if ($Value) { [void][int]::TryParse($Value, [ref]$scanMax) }
            Invoke-Scan -Limit $scanMax
        }
        'bench'  { Invoke-Bench }
        'raw'    {
            if (-not $Value) { throw 'raw 需要功能号，例如: .\mifs.ps1 raw 19' }
            $out = Invoke-Mifs -Type $GET -Func ([int]$Value)
            Write-Host ("fn={0} -> {1}" -f $Value, (Format-Bytes $out))
        }
        'mode'   {
            if (-not $Value -or -not $ModeValue.Contains($Value)) { throw 'mode 需要 low / balanced / performance（或 低功耗/均衡/性能）' }
            Set-MifsByte -Func $FNUM['PER_MODE'] -Val ([byte]$ModeValue[$Value])
            Start-Sleep -Milliseconds 300
            Write-Host ("已发送: 切到 {0}" -f $Value) -ForegroundColor Green
            Write-MifsLog 'INFO ' ("mode => {0}（写值 {1}），读回 {2}" -f $Value, $ModeValue[$Value], (Get-MifsByte -Func $FNUM['PER_MODE']))
            Show-Status
        }
        'fanboost' {
            if ($Value -notin 'on', 'off') { throw 'fanboost 需要 on / off' }
            $before = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
            $b = New-Object byte[] 2
            $b[0] = 0
            $b[1] = [byte]$(if ($Value -eq 'on') { 1 } else { 0 })
            Invoke-Mifs -Type $SET -Func $FNUM['MAX_FAN_SWITCH'] -SetPayload $b | Out-Null
            Start-Sleep -Milliseconds 400
            $after = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
            Write-Host ("已发送: 风扇满速 {0}    （写前读回={1}  写后读回={2}）" -f $Value, $before, $after) -ForegroundColor Green
            Write-MifsLog 'INFO ' ("fanboost => {0}（写前 {1} 写后 {2}）" -f $Value, $before, $after)
            if ($null -eq $before -or $null -eq $after) {
                Write-Host '  ⚠️ 该功能号本机不可读，风扇满速很可能未实现。' -ForegroundColor Yellow
            }
            elseif ([int]$before -eq [int]$after) {
                Write-Host '  ⚠️ 值没有变化 —— EC 忽略了这次写入。' -ForegroundColor Yellow
                $acv = Get-MifsByte -Func $FNUM['AC_TYPE']
                if ($null -ne $acv -and [int]$acv -eq 1) {
                    Write-Host '  ⚠️ 但注意供电类型：现在是 Type-C(PD) 供电。上游 tongfang-mifs-wmi 驱动文档写明' -ForegroundColor Yellow
                    Write-Host '     「性能/满速模式与风扇满速在 Type-C 供电下被硬件禁用，只有圆口 DC 电源才放开」。' -ForegroundColor Yellow
                    Write-Host '     请插上圆口电源后重试，别据此判定"本机不支持"。' -ForegroundColor Yellow
                } else {
                    Write-Host '  ⚠️ 供电类型不是 Type-C，可认为本机 EC 确实忽略该开关。' -ForegroundColor Yellow
                }
            }
            else {
                Write-Host '  ✅ 值已改变，开关生效。' -ForegroundColor Green
            }
        }
        'kbd'    {
            if (-not $Value) { throw 'kbd 需要 0~3' }
            Set-MifsByte -Func $FNUM['RGB_BRIGHT'] -Val ([byte][int]$Value)
            Write-Host ("已发送: 键盘背光 {0}" -f $Value) -ForegroundColor Green
            Write-MifsLog 'INFO ' ("kbd => {0}" -f $Value)
        }
        'osd'    {
            switch ($Value) {
                'status'   { Show-OsdStatus }
                'restart'  { Invoke-OsdRestart }
                'diagnose' { Invoke-OsdDiagnose }
                'dpi'      { Write-Host ("DPI 兼容标记: {0}" -f $(if (Test-OsdDpiEnabled) { "已应用（$($script:DpiVal)）" } else { '未应用' })) }
                'dpi-on'   { Set-OsdDpiFlag -Enable $true }
                'dpi-off'  { Set-OsdDpiFlag -Enable $false }
                default    { throw 'osd 需要 status / restart / diagnose / dpi / dpi-on / dpi-off' }
            }
        }
        'startup' {
            switch ($Value) {
                'status' { $t = Get-StartupTask; Write-Host ("开机自启: {0}" -f $(if ($t.NeedsRepair) { '已启用但缺 --tray（登录会弹窗），跑 startup on 重建' } elseif ($t.Enabled) { '已启用（--tray，静默进托盘）' } else { '未启用' })) -ForegroundColor $(if ($t.NeedsRepair) { 'Yellow' } elseif ($t.Enabled) { 'Green' } else { 'DarkGray' }) }
                'on'     { Set-StartupTask -Enable $true }
                'off'    { Set-StartupTask -Enable $false }
                default  { throw 'startup 需要 status / on / off' }
            }
        }
        'log'    { Show-Log }
        'fan'    {
            switch ($Value) {
                'test'  { Invoke-FanTest }
                default { Invoke-FanTest }
            }
        }
    }
}
catch {
    Write-Host ''
    Write-Host ('执行失败: {0}' -f $_.Exception.Message) -ForegroundColor Red
    Write-Host ''
    Write-Host '排查：' -ForegroundColor Yellow
    Write-Host '  1) 确认是以管理员身份运行'
    Write-Host '  2) 跑  .\mifs.ps1 probe  看调用链解析结果'
    Write-Host '  3) 若 WMI 类不存在，说明本机 BIOS 未暴露 MIFS 接口'
    exit 1
}
