<#
  OpenMIFS — 同方 MIFS (MiInterface) 图形控制台
  ==================================================================
  不依赖任何官方控制中心组件。纯 PowerShell + WinForms，单文件、零依赖。

  工作原理与功能号详见 src/mifs.ps1 顶部注释与 docs/PROTOCOL.md
  ACPI 设备 : ACPI\PNP0C14\MIFS      WMI 类 : root\wmi:MICommonInterface
  ACPI GUID : {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}

  能力探测
    功能号读不到值 → 对应控件直接 Enabled=$false 并置灰，不靠点下去才报错。
    风扇满速比较特殊：EC 可能「接受写入但不改值」，所以首次点击时做写后读回校验，
    结论记在 %LOCALAPPDATA%\OpenMIFS\capabilities.txt（fanboost=supported|unsupported|unknown），
    与 exe 版共用。想重新实测，点界面上的「重测功能」。

  日志
    与命令行版 src\mifs.ps1、exe 版共用 %LOCALAPPDATA%\OpenMIFS\openmifs.log（超过 1 MB 轮转）。
    自动刷新不写日志，只在接口错误文本变化时记一次。

  用法
    双击根目录的 mifs-gui.cmd（会自动 UAC 提权）
    或  powershell -ExecutionPolicy Bypass -File src\mifs-gui.ps1

  界面自检（不提权、不弹窗）
    powershell -ExecutionPolicy Bypass -File src\mifs-gui.ps1 -SmokeTest
#>
[CmdletBinding()]
param(
    [switch]$SmokeTest
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:AppVersion = '0.2.1'

# ──────────────────────────────── 日志（与 CLI 版 / exe 版共用 %LOCALAPPDATA%\OpenMIFS\openmifs.log）
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
        $line = '{0} [{1}] pid={2} tid={3} | (GUI) {4}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss.fff'),
            $Level.PadRight(5), $PID, [System.Threading.Thread]::CurrentThread.ManagedThreadId, $Message
        [System.IO.File]::AppendAllText($script:LogFile, $line + [Environment]::NewLine, $noBom)
    }
    catch { }
}

function Write-MifsEx {
    param([string]$Context, $ErrorRecord)
    $ex = $null
    if ($ErrorRecord -and $ErrorRecord.Exception) { $ex = $ErrorRecord.Exception } else { $ex = $ErrorRecord }
    if (-not $ex) { Write-MifsLog 'ERROR' "$Context：未知异常"; return }
    $detail = '{0}: {1}' -f $ex.GetType().Name, $ex.Message
    try {
        if ($ex.StackTrace) {
            $first = ($ex.StackTrace -split "`r?`n")[0].Trim()
            if ($first) { $detail += ' @ ' + $first }
        }
    }
    catch { }
    if ($ex.InnerException) { $detail += '  ← ' + $ex.InnerException.GetType().Name + ': ' + $ex.InnerException.Message }
    Write-MifsLog 'ERROR' "$Context：$detail"
}

# ──────────────────────────────── 自提权
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $SmokeTest -and -not $isAdmin) {
    Write-MifsLog 'INFO ' '未提权，正在请求 UAC 提权后重启'
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        )
        exit 0
    }
    catch {
        Write-MifsEx '请求提权失败' $_
        [System.Windows.Forms.MessageBox]::Show("需要管理员权限才能访问 MIFS 接口。`n`n$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Error') | Out-Null
        exit 1
    }
}

# ──────────────────────────────── 协议常量
$Namespace = 'root\wmi'
$ClassName = 'MICommonInterface'
$Method    = 'MiInterface'
$GET       = 250
$SET       = 251

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

# 性能模式映射【按需修改】—— 本机实测 0=性能 1=均衡 2=低功耗
$ModeValue = [ordered]@{ '低功耗' = 2; '均衡' = 1; '性能' = 0 }
$ModeLabel = @{ 0 = '性能'; 1 = '均衡'; 2 = '低功耗'; 3 = '满速' }
$ModeOrder = @('低功耗', '均衡', '性能')

# ──────────────────────────────── 调用层
$script:Target = $null
$script:TargetHow = '未解析'
$script:LastMifsError = ''
$script:LastLoggedError = ''

function Resolve-MifsTarget {
    try {
        $inst = Get-CimInstance -Namespace $Namespace -ClassName $ClassName -ErrorAction Stop
        if ($inst) {
            $script:TargetHow = 'Get-CimInstance'
            return [pscustomobject]@{ Kind = 'instance'; Obj = @($inst)[0] }
        }
    }
    catch { }
    try {
        $mc = [wmiclass]"\\.\$Namespace`:$ClassName"
        $null = $mc.GetMethodParameters($Method)
        $script:TargetHow = 'legacy DCOM'
        return [pscustomobject]@{ Kind = 'legacy'; Obj = $mc }
    }
    catch { }
    $script:TargetHow = 'class-level'
    return [pscustomobject]@{ Kind = 'class'; Obj = $null }
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
    if (-not $out) { throw '返回空数据' }
    return , ([byte[]]$out)
}

function Get-MifsByte {
    param([int]$Func)
    try { return (Invoke-Mifs -Type $GET -Func $Func)[4] }
    catch { $script:LastMifsError = $_.Exception.Message; return $null }
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
    catch { $script:LastMifsError = $_.Exception.Message; return $null }
}

function Set-MifsByte {
    param([int]$Func, [byte]$Val)
    $b = New-Object byte[] 1
    $b[0] = $Val
    Invoke-Mifs -Type $SET -Func $Func -SetPayload $b | Out-Null
}

# 写一次 SET，等一会儿，再读回：日志里留下「写值 + 读回值」，这是判断 EC 是否真的接受的唯一依据
function Set-MifsByteLogged {
    param([int]$Func, [byte]$Val, [string]$Label, [int]$DelayMs = 200)
    Set-MifsByte -Func $Func -Val $Val
    Start-Sleep -Milliseconds $DelayMs
    $back = Get-MifsByte -Func $Func
    $backText = if ($null -ne $back) { [string][int]$back } else { '读取失败' }
    Write-MifsLog 'INFO ' ("{0}（写 fn={1} 值={2}），读回 {3}" -f $Label, $Func, $Val, $backText)
}

# ──────────────────────────────── 能力探测缓存（键 fanboost，与 exe 版共用同一份文件）
$script:CapsFile       = Join-Path $script:LogDir 'capabilities.txt'
$script:FanBoostCap    = $null    # $null=未检测  $true=可用  $false=本机未实现
$script:FanBoostUsable = $false
$script:FanBoostOn     = $false

function Get-CapText {
    if ($null -eq $script:FanBoostCap) { return 'unknown' }
    if ($script:FanBoostCap) { return 'supported' }
    return 'unsupported'
}

function Load-Caps {
    try {
        $script:FanBoostCap = $null
        if (-not (Test-Path $script:CapsFile)) { return }
        foreach ($raw in @(Get-Content $script:CapsFile -Encoding UTF8 -ErrorAction Stop)) {
            $s = ([string]$raw).Trim()
            if (-not $s -or $s.StartsWith('#')) { continue }
            $eq = $s.IndexOf('=')
            if ($eq -le 0) { continue }
            $k = $s.Substring(0, $eq).Trim().ToLowerInvariant()
            $v = $s.Substring($eq + 1).Trim().ToLowerInvariant()
            if ($k -eq 'fanboost') {
                if ($v -eq 'supported') { $script:FanBoostCap = $true }
                elseif ($v -eq 'unsupported') { $script:FanBoostCap = $false }
                else { $script:FanBoostCap = $null }
            }
        }
        Write-MifsLog 'INFO ' ("加载能力缓存：{0} → 风扇满速={1}" -f $script:CapsFile, (Get-CapText))
    }
    catch { Write-MifsEx '读取能力缓存失败' $_ }
}

function Save-Caps {
    try {
        if (-not (Test-Path $script:LogDir)) { New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null }
        $text = "# OpenMIFS 能力探测缓存（程序自动生成，删掉即视为未检测）`r`nfanboost=" + (Get-CapText) + "`r`n"
        [System.IO.File]::WriteAllText($script:CapsFile, $text, (New-Object System.Text.UTF8Encoding($false)))
    }
    catch { Write-MifsEx '写入能力缓存失败' $_ }
}

# ──────────────────────────────── 外部命令（sc / taskkill / schtasks）统一入口
function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # 5.1 下把 stderr 重定向到输出流时，Stop 会把普通错误当成终止错误
    $code = 0
    try {
        $out = & $File @Arguments 2>&1
        $code = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prev }
    # 原生程序的 stderr 在 5.1 里是 ErrorRecord，直接 [string] 会附带 RemoteException 类型名；
    # 而且同一条 stderr 会额外多出一条内容为空的 ErrorRecord，这里一并丢掉
    $parts = foreach ($item in $out) {
        if ($item -is [System.Management.Automation.ErrorRecord]) {
            $m = ''
            if ($item.Exception -and $item.Exception.Message) { $m = [string]$item.Exception.Message }
            if ($m) { $m }
        }
        else { [string]$item }
    }
    [pscustomobject]@{
        ExitCode = $code
        Text     = (($parts -join ' ').Trim() -replace '\s+', ' ')
    }
}

# ──────────────────────────────── OSD（Fn 屏幕提示）
$script:OsdService      = 'BLDHotKeyService'
$script:OsdUtility      = 'BLDFnHotkeyUtility.exe'
$script:OsdDir          = 'C:\Program Files\OSD'
$script:OsdDetail       = '检测中…'
$script:LastOsdCheck    = [datetime]::MinValue
$script:LastStartupCheck = [datetime]::MinValue

function Get-OsdState {
    $err = ''
    $svc = $null
    try { $svc = Get-CimInstance Win32_Service -Filter "Name='$($script:OsdService)'" -ErrorAction Stop }
    catch { $err = $_.Exception.Message }

    $procs = @()
    try { $procs = @(Get-CimInstance Win32_Process -Filter "Name='$($script:OsdUtility)'" -ErrorAction Stop) }
    catch { if (-not $err) { $err = $_.Exception.Message } }

    $svcSince = $null
    if ($svc -and $svc.ProcessId) {
        try { $svcSince = (Get-CimInstance Win32_Process -Filter "ProcessId=$($svc.ProcessId)" -ErrorAction Stop).CreationDate }
        catch { }
    }

    [pscustomobject]@{
        Found        = [bool]$svc
        ServiceState = $(if ($svc) { $svc.State } else { '未安装' })
        ServiceStart = $(if ($svc) { $svc.StartMode } else { '' })
        ServicePid   = $(if ($svc) { $svc.ProcessId } else { 0 })
        ServiceSince = $svcSince
        UtilityCount = $procs.Count
        UtilityPids  = (($procs | ForEach-Object { $_.ProcessId }) -join ', ')
        UtilitySince = $(if ($procs.Count -gt 0) { $procs[0].CreationDate } else { $null })
        Error        = $err
    }
}

function Get-OsdDetail {
    param($State)
    if (-not $State.Found -and $State.Error) { return "查询失败（$($State.Error)）" }
    $svc = if ($State.Found) { "服务 $($State.ServiceState)" } else { '服务未安装' }
    $app = if ($State.UtilityCount -gt 0) { "进程运行中 $($State.UtilityCount) 个" } else { '进程未运行' }
    $flag = if ($State.Found -and $State.ServiceState -eq 'Running' -and $State.UtilityCount -gt 0) { ' · ' } else { ' · 异常 · ' }
    return "$svc$flag$app"
}

function Test-OsdHealthy {
    param($State)
    return ($State.Found -and $State.ServiceState -eq 'Running' -and $State.UtilityCount -gt 0)
}

# 8 秒最多查一次：自动刷新每 3 秒一次，不能每次都去打 WMI
function Update-OsdStatus {
    param([switch]$Force)
    if (-not $Force -and ((Get-Date) - $script:LastOsdCheck).TotalSeconds -lt 8) { return $script:OsdDetail }
    $script:LastOsdCheck = Get-Date
    try {
        $s = Get-OsdState
        $script:OsdDetail = Get-OsdDetail -State $s
        $lblOsd.Text      = 'OSD：' + $script:OsdDetail
        $lblOsd.ForeColor = $(if (Test-OsdHealthy -State $s) { $ColorOk } else { $ColorWarn })
    }
    catch {
        Write-MifsEx '查询 OSD 状态失败' $_
        $script:OsdDetail = '查询失败'
        $lblOsd.Text      = 'OSD：查询失败'
        $lblOsd.ForeColor = $ColorWarn
    }
    return $script:OsdDetail
}

function Invoke-OsdRestart {
    $log = New-Object System.Collections.Generic.List[string]

    $r1 = Invoke-Native 'sc.exe' @('stop', $script:OsdService)
    $log.Add(("sc stop  → exit {0} {1}" -f $r1.ExitCode, $r1.Text))
    Write-MifsLog 'INFO ' ("OSD 重启：sc stop exit={0} {1}" -f $r1.ExitCode, $r1.Text)

    $r2 = Invoke-Native 'sc.exe' @('start', $script:OsdService)
    $log.Add(("sc start → exit {0} {1}" -f $r2.ExitCode, $r2.Text))
    Write-MifsLog 'INFO ' ("OSD 重启：sc start exit={0} {1}" -f $r2.ExitCode, $r2.Text)

    $r3 = Invoke-Native 'taskkill.exe' @('/IM', $script:OsdUtility, '/F')
    $log.Add(("taskkill → exit {0} {1}" -f $r3.ExitCode, $r3.Text))
    Write-MifsLog 'INFO ' ("OSD 重启：taskkill exit={0} {1}" -f $r3.ExitCode, $r3.Text)

    Start-Sleep -Milliseconds 1200
    $exePath = Join-Path $script:OsdDir $script:OsdUtility
    if (Test-Path $exePath) {
        try {
            Start-Process -FilePath $exePath -WorkingDirectory $script:OsdDir
            $log.Add("已重新启动 $exePath")
            Write-MifsLog 'INFO ' ("OSD 重启：已启动 {0}" -f $exePath)
        }
        catch {
            $log.Add("启动 OSD 失败：$($_.Exception.Message)")
            Write-MifsEx '启动 OSD 界面失败' $_
        }
    }
    else {
        $log.Add("未找到 $exePath（官方 OSD 包没装？）")
        Write-MifsLog 'WARN ' ("OSD 重启：未找到 {0}" -f $exePath)
    }
    return ($log -join "`r`n")
}

function Invoke-OsdDiagnose {
    $L = New-Object System.Collections.Generic.List[string]
    $s = Get-OsdState

    $L.Add('===== OSD 诊断 =====')
    $L.Add('-- 服务 / 进程 --')
    $L.Add(("服务          : {0} / {1}  PID={2}  启动于 {3}" -f $script:OsdService, $s.ServiceState, $s.ServicePid, $s.ServiceSince))
    $L.Add(("界面进程      : {0} 个  PID={1}  启动于 {2}" -f $s.UtilityCount, $s.UtilityPids, $s.UtilitySince))
    if ($s.Error) { $L.Add(("查询错误      : {0}" -f $s.Error)) }
    $L.Add('')

    $L.Add('-- 安装目录 --')
    if (Test-Path $script:OsdDir) {
        foreach ($f in @(Get-ChildItem $script:OsdDir -ErrorAction SilentlyContinue)) {
            $L.Add(("  {0,-34} {1,10:N0} 字节  {2}" -f $f.Name, $f.Length, $f.LastWriteTime.ToString('yyyy-MM-dd HH:mm')))
        }
    }
    else { $L.Add('  目录不存在') }
    $L.Add('')

    $L.Add('-- 服务安装日志尾部 --')
    $ilog = Join-Path $script:OsdDir 'BLDHotKeyService.InstallLog'
    if (Test-Path $ilog) {
        foreach ($ln in @(Get-Content $ilog -Tail 12 -ErrorAction SilentlyContinue)) { $L.Add("  $ln") }
    }
    else { $L.Add('  没有 InstallLog') }
    $L.Add('')

    # ── 事件投递：Fn 事件到底有没有送到 OSD 进程
    $ev = Get-OsdEventSummary
    $L.Add('-- OSD 事件投递（OSDEvents）--')
    if ($ev.OsdCount -gt 0) {
        $L.Add(("  条数          : {0}（最近优先扫描范围内）" -f $ev.OsdCount))
        $L.Add(("  最新一条      : {0}" -f $ev.OsdNewest))
        if ($ev.OsdSamples.Count -gt 0) { $L.Add(("  样例负载      : {0}" -f ($ev.OsdSamples -join ' / '))) }
        $L.Add('  判读          : 有近期条目 = Fn 事件仍在送达 OSD 进程，接收环节正常；')
        $L.Add('                  问题出在「画出来」这一步（DPI / 分层窗口 / 合成），不是热键坏了。')
    }
    else {
        $L.Add('  没有 OSDEvents 条目。')
        $L.Add('  判读          : 要么本机不用这个日志源，要么 Fn 事件根本没送到 OSD 进程。')
        $L.Add('                  按一次 Fn 组合键后再跑一次诊断，对比条数是否增加。')
    }
    if ($ev.OsdError) { $L.Add(("  查询提示      : {0}" -f $ev.OsdError)) }
    $L.Add('')

    $L.Add('-- 服务心跳（BLDHotKeyServiceEvent）--')
    if ($ev.HbCount -gt 0) {
        $L.Add(("  心跳          : 最近优先扫描到 {0} 条，最新 {1}" -f $ev.HbCount, $ev.HbNewest))
        $L.Add(("  最早一条      : {0}（心跳正常时每 5 秒一条）" -f $ev.HbOldest))
        $L.Add('  判读          : 最新一条距今很久 = 服务这侧也断了；心跳在跑也不代表提示能显示出来。')
    }
    else { $L.Add('  没有 BLDHotKeyServiceEvent 条目。') }
    if ($ev.HbError) { $L.Add(("  查询提示      : {0}" -f $ev.HbError)) }
    $L.Add('')

    $L.Add('-- 显示环境 / DPI（分层窗口画不出来的头号嫌疑）--')
    try {
        foreach ($sc in [System.Windows.Forms.Screen]::AllScreens) {
            $L.Add(("  {0} {1}x{2} @ ({3},{4})" -f $(if ($sc.Primary) { '主屏' } else { '副屏' }), $sc.Bounds.Width, $sc.Bounds.Height, $sc.Bounds.Left, $sc.Bounds.Top))
        }
    }
    catch { $L.Add('  查询失败') }
    $dpi = Get-SystemDpiInfo
    $pct = if ($dpi.SystemDpi -gt 0) { [int][Math]::Round($dpi.SystemDpi / 96 * 100) } else { 100 }
    $L.Add(("  系统缩放      : {0} DPI（约 {1}%）  来源 HKCU\Control Panel\Desktop\WindowMetrics\AppliedDPI" -f $dpi.SystemDpi, $pct))
    $L.Add(("  进程读到 DPI  : {0}（Graphics::FromHwnd）{1}" -f $dpi.ProcDpi,
        $(if ($dpi.ProcDpi -ne $dpi.SystemDpi) { '  ← 本进程不是 DPI 感知的，读到的是虚拟化值，以系统缩放为准' } else { '' })))
    if ($pct -ne 100) {
        $L.Add('  判读          : 系统缩放不是 100%，而 OSD 界面进程是 DPI 不感知的（清单只有 asInvoker），')
        $L.Add('                  它用 UpdateLayeredWindow 画的提示可能静默失效 —— 当前头号嫌疑。')
    }
    else { $L.Add('  判读          : 缩放 100%，DPI 这条嫌疑下降。') }
    $ds = Get-DpiCompatState
    $L.Add("  DPI 兼容标记  : " + $(if ($ds.Applied) { "已应用（$script:DpiCompatFlag）" } else { '未应用' }) +
        $(if ($ds.Exists) { '' } else { '   （未安装官方 OSD）' }))
    $L.Add(("  标记注册表项  : {0}\{1}" -f 'HKLM', $script:DpiCompatSubKey))
    $L.Add('')

    $L.Add('-- 结论提示（嫌疑从高到低）--')
    $L.Add('  1) DPI / 分层窗口：勾选「DPI 兼容修复」→ 点「重启 OSD」→ 按一次 Fn 看提示是否出现；')
    $L.Add('     或临时把缩放改成 100% 做对照。')
    $L.Add('  2) Fn 事件是否送达：看上面 OSDEvents 有没有近期条目。有 = 接收正常，坏在显示。')
    $L.Add('  3) 服务触发链路：看心跳最新一条距今多久。停写很久 = 服务这侧也断了。')
    $L.Add('')

    $L.Add(("日志文件      : {0}" -f $script:LogFile))
    $L.Add('更多证据（System 日志、崩溃/拦截记录、写入 osd-diagnose.txt）请用 exe 版：')
    $L.Add('  OpenMIFS.exe --diagnose     →  %LOCALAPPDATA%\OpenMIFS\osd-diagnose.txt')

    $text = $L -join "`r`n"
    $txtStatus.Text = $text
    Write-MifsLog 'INFO ' ("OSD 诊断完成；{0}" -f (Get-OsdDetail -State $s))
    Write-MifsLog 'INFO ' ("OSD 诊断明细：" + (($L | Where-Object { $_ -ne '' }) -join ' / '))
    return $text
}

# ──────────────────────────────── OSD 事件日志摘要（最新优先，别被 5 秒一条的心跳吃光额度）
function Get-OsdEventSummary {
    $r = [pscustomobject]@{
        OsdCount = 0; OsdNewest = ''; OsdSamples = @(); OsdError = ''
        HbCount  = 0; HbNewest  = ''; HbOldest   = ''; HbError  = ''
    }
    # Get-WinEvent 默认按时间倒序返回，-MaxEvents N 取到的就是「最新的 N 条」。
    # 千万不要正序读再截断：BLDHotKeyServiceEvent 每 5 秒一条，正序会被心跳吃光额度，
    # 从而得出「心跳早停了」这种完全错误的结论。
    $evErr = $null
    $osd = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'OSDEvents' } -MaxEvents 200 -ErrorAction SilentlyContinue -ErrorVariable evErr)
    $r.OsdCount = $osd.Count
    if ($osd.Count -gt 0) {
        $r.OsdNewest = $osd[0].TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
        $samples = New-Object System.Collections.Generic.List[string]
        foreach ($e in $osd) {
            if ($samples.Count -ge 8) { break }
            $m = ''
            try { $m = ([string]$e.Message).Replace("`r", ' ').Replace("`n", ' ').Trim() } catch { }
            if ($m) {
                if ($m.Length -gt 80) { $m = $m.Substring(0, 80) }
                if (-not $samples.Contains($m)) { $samples.Add($m) }
            }
        }
        $r.OsdSamples = $samples.ToArray()
    }
    elseif ($evErr) {
        $m = [string]$evErr[0].Exception.Message
        if ($m -notmatch '(?i)no events|未找到|没有找到') { $r.OsdError = $m }
    }

    $evErr2 = $null
    $hb = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'BLDHotKeyServiceEvent' } -MaxEvents 1200 -ErrorAction SilentlyContinue -ErrorVariable evErr2)
    $r.HbCount = $hb.Count
    if ($hb.Count -gt 0) {
        $r.HbNewest = $hb[0].TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
        $r.HbOldest = $hb[$hb.Count - 1].TimeCreated.ToString('yyyy-MM-dd HH:mm:ss')
    }
    elseif ($evErr2) {
        $m = [string]$evErr2[0].Exception.Message
        if ($m -notmatch '(?i)no events|未找到|没有找到') { $r.HbError = $m }
    }
    return $r
}

# ──────────────────────────────── OSD DPI 兼容修复（实验，可撤销）
# 背景：BLDFnHotkeyUtility.exe 是 DPI 不感知进程（清单只有 asInvoker），提示用
# UpdateLayeredWindow 画分层窗口，125%/150% 缩放下可能静默失效（不报错、不崩溃、日志照写）。
# Windows 自带的兼容性标记可强制它按系统 DPI 渲染：
#   HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers
#   值名 = 程序完整路径，值 = "~ HIGHDPIAWARE"
# 这就是「属性 → 兼容性 → 更改高 DPI 设置」写的同一条注册表项，删掉即还原。
$script:DpiCompatSubKey = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers'
$script:DpiCompatExe    = Join-Path $script:OsdDir $script:OsdUtility
$script:DpiCompatFlag   = '~ HIGHDPIAWARE'
$script:DpiHint         = ''

function Get-SystemDpiInfo {
    # 注意：powershell.exe 不是 DPI 感知进程，Graphics::FromHwnd 拿到的是被虚拟化过的 96，
    # 125% 缩放时会误判成「100%，嫌疑下降」。所以以注册表 AppliedDPI 为准（96/120/144）。
    $regDpi = 0
    $procDpi = 0
    try {
        $regDpi = [int](Get-ItemProperty -Path 'HKCU:\Control Panel\Desktop\WindowMetrics' -Name 'AppliedDPI' -ErrorAction Stop).AppliedDPI
    }
    catch { }
    try {
        $gfx = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
        try { $procDpi = [int][Math]::Round($gfx.DpiX) } finally { $gfx.Dispose() }
    }
    catch { }
    [pscustomobject]@{
        SystemDpi = $(if ($regDpi -gt 0) { $regDpi } else { $procDpi })
        ProcDpi   = $procDpi
    }
}

function Get-DpiCompatState {
    $value = $null
    $err = ''
    try {
        # 必须用 64 位视图，否则 32 位进程会读到 Wow6432Node 下的另一份
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $sub = $base.OpenSubKey($script:DpiCompatSubKey)
            if ($sub) {
                try { $value = [string]$sub.GetValue($script:DpiCompatExe, $null) } finally { $sub.Close() }
            }
        }
        finally { $base.Close() }
    }
    catch { $err = $_.Exception.Message }
    [pscustomobject]@{
        Exists  = (Test-Path $script:DpiCompatExe)
        Applied = [bool]($value -and $value.ToUpperInvariant().Contains('HIGHDPIAWARE'))
        Value   = $value
        Error   = $err
    }
}

function Set-DpiCompat {
    param([bool]$Enable)
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $sub = $base.CreateSubKey($script:DpiCompatSubKey)
        if (-not $sub) { throw '无法打开 AppCompatFlags\Layers 注册表项（需要管理员权限）' }
        try {
            if ($Enable) {
                $sub.SetValue($script:DpiCompatExe, $script:DpiCompatFlag, [Microsoft.Win32.RegistryValueKind]::String)
                Write-MifsLog 'INFO ' ("DPI 兼容修复：写入 {0} = {1}" -f $script:DpiCompatExe, $script:DpiCompatFlag)
            }
            else {
                $sub.DeleteValue($script:DpiCompatExe, $false)
                Write-MifsLog 'INFO ' ("DPI 兼容修复：已删除 {0} 的兼容性标记" -f $script:DpiCompatExe)
            }
        }
        finally { $sub.Close() }
    }
    finally { $base.Close() }
}

# 同步复选框：调用方负责用 $script:Suppress 包住（Refresh-All 内本来就是 $true）
function Update-DpiCompatStatus {
    try {
        $s = Get-DpiCompatState
        $chkDpi.Checked = $s.Applied
        $chkDpi.Enabled = $s.Exists
        if (-not $s.Exists) {
            $lblDpi.Text = '（未安装官方 OSD）'
            $lblDpi.ForeColor = $ColorDim
            return '（未安装官方 OSD）'
        }
        $lblDpi.Text = $(if ($s.Applied) { "已应用 $script:DpiCompatFlag" } else { '未应用' })
        $lblDpi.ForeColor = $(if ($s.Applied) { $ColorOk } else { $ColorDim })
        if ($s.Error) { Write-MifsLog 'WARN ' ("读取 OSD DPI 兼容标记失败：{0}" -f $s.Error) }
        return $(if ($s.Applied) { "已应用 $script:DpiCompatFlag" } else { '未应用' })
    }
    catch {
        Write-MifsEx '刷新 DPI 兼容修复状态失败' $_
        return '查询失败'
    }
}

# ──────────────────────────────── 开机自启（计划任务，避免注册表 Run 每次登录弹 UAC）
function Get-StartupTask {
    $r = Invoke-Native 'schtasks.exe' @('/Query', '/TN', 'OpenMIFS')
    [pscustomobject]@{ Enabled = ($r.ExitCode -eq 0); Output = $r.Text }
}

function Set-StartupTask {
    param([bool]$Enable)
    $exePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\OpenMIFS.exe'
    if ($Enable) {
        if (-not (Test-Path $exePath)) {
            Write-MifsLog 'WARN ' ("开机自启：找不到 {0}，请先构建（build\build.ps1）" -f $exePath)
            throw "找不到 $exePath —— 请先构建（build\build.ps1）后再开启开机自启"
        }
        $r = Invoke-Native 'schtasks.exe' @('/Create', '/TN', 'OpenMIFS', '/TR', "`"$exePath`"",
            '/SC', 'ONLOGON', '/RL', 'HIGHEST', '/F', '/DELAY', '0000:10')
    }
    else {
        $r = Invoke-Native 'schtasks.exe' @('/Delete', '/TN', 'OpenMIFS', '/F')
    }
    Write-MifsLog 'INFO ' ("开机自启 => {0}（exit={1}）{2}" -f $(if ($Enable) { 'on' } else { 'off' }), $r.ExitCode, $r.Text)
    return $r.ExitCode
}

# 20 秒最多查一次（外部进程调用，别每 3 秒一次）
function Update-StartupStatus {
    param([switch]$Force)
    if (-not $Force -and ((Get-Date) - $script:LastStartupCheck).TotalSeconds -lt 20) { return $chkStartup.Checked }
    $script:LastStartupCheck = Get-Date
    try {
        $on = (Get-StartupTask).Enabled
        $chkStartup.Checked = $on
        $chkStartup.Text = $(if ($on) { '开机自启（已启用 · 计划任务）' } else { '开机自启（计划任务 · 免 UAC）' })
    }
    catch { Write-MifsEx '查询开机自启状态失败' $_ }
    return $chkStartup.Checked
}

# ──────────────────────────────── 界面
[System.Windows.Forms.Application]::EnableVisualStyles()

$FontUI  = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)
$FontUI8 = New-Object System.Drawing.Font('Microsoft YaHei UI', 8)
$FontBold = New-Object System.Drawing.Font('Microsoft YaHei UI', 9, [System.Drawing.FontStyle]::Bold)
$FontMono = New-Object System.Drawing.Font('Consolas', 9)
$ColorOk   = [System.Drawing.Color]::FromArgb(22, 128, 61)
$ColorWarn = [System.Drawing.Color]::FromArgb(185, 28, 28)
$ColorDim  = [System.Drawing.Color]::FromArgb(130, 130, 130)

$form                 = New-Object System.Windows.Forms.Form
$form.Text            = 'OpenMIFS — 同方 MIFS 控制台'
$form.ClientSize      = New-Object System.Drawing.Size(480, 712)
$form.StartPosition   = 'CenterScreen'
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox     = $false
$form.Font            = $FontUI

function New-Group($text, $y, $h) {
    $g = New-Object System.Windows.Forms.GroupBox
    $g.Text     = $text
    $g.Location = New-Object System.Drawing.Point(12, $y)
    $g.Size     = New-Object System.Drawing.Size(456, $h)
    $g.Font     = $FontUI8
    $form.Controls.Add($g)
    return $g
}

$script:Suppress = $false

# ── 顶部状态条：两行 8pt 中文，高度 46 给足，避免第二行被下面的分组框压掉
$lblHeader           = New-Object System.Windows.Forms.Label
$lblHeader.Location  = New-Object System.Drawing.Point(14, 6)
$lblHeader.Size      = New-Object System.Drawing.Size(452, 46)
$lblHeader.AutoSize  = $false
$lblHeader.TextAlign = 'TopLeft'
$lblHeader.Font      = $FontUI8
$lblHeader.Text      = '正在检测接口…'
$form.Controls.Add($lblHeader)

# ── 性能模式
$gMode = New-Group '性能模式' 58 68
$script:BtnMode = @{}
$x = 12
foreach ($mn in $ModeOrder) {
    $b          = New-Object System.Windows.Forms.Button
    $b.Text     = $mn
    $b.Location = New-Object System.Drawing.Point($x, 24)
    $b.Size     = New-Object System.Drawing.Size(140, 30)
    $b.Tag      = $mn
    $b.Font     = $FontUI
    $b.Add_Click({
        param($s, $e)
        if ($script:Suppress) { return }
        $name = $s.Tag
        try {
            Set-MifsByteLogged -Func $FNUM['PER_MODE'] -Val ([byte]$ModeValue[$name]) -Label "性能模式 → $name" -DelayMs 250
            Refresh-All
        }
        catch {
            Write-MifsEx '切换性能模式失败' $_
            [System.Windows.Forms.MessageBox]::Show("切换失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        }
    })
    $gMode.Controls.Add($b)
    $script:BtnMode[$mn] = $b
    $x += 148
}

# ── 风扇
$gFan           = New-Group '风扇' 132 84
$lblFan         = New-Object System.Windows.Forms.Label
$lblFan.Location = New-Object System.Drawing.Point(12, 22)
$lblFan.Size     = New-Object System.Drawing.Size(432, 20)
$lblFan.Font     = $FontUI8
$lblFan.Text     = '读取中…'
$gFan.Controls.Add($lblFan)

$btnBoost          = New-Object System.Windows.Forms.Button
$btnBoost.Location = New-Object System.Drawing.Point(12, 46)
$btnBoost.Size     = New-Object System.Drawing.Size(200, 28)
$btnBoost.Font     = $FontUI
$btnBoost.Text     = '风扇满速：关'
$btnBoost.Add_Click({
    if ($script:Suppress) { return }
    if (-not $script:FanBoostUsable) {
        Write-MifsLog 'INFO ' '风扇满速：按钮不可用（能力探测判定本机未实现）'
        return
    }
    try {
        $target = if ($script:FanBoostOn) { 0 } else { 1 }
        $before = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
        $beforeText = if ($null -ne $before) { [string][int]$before } else { '读取失败' }
        Write-MifsLog 'INFO ' ("风扇满速 → {0}（写 fn=20 值={1}），写前读回 {2}" -f $(if ($target -eq 1) { '开' } else { '关' }), $target, $beforeText)

        $b = New-Object byte[] 2
        $b[0] = 0
        $b[1] = [byte]$target
        Invoke-Mifs -Type $SET -Func $FNUM['MAX_FAN_SWITCH'] -SetPayload $b | Out-Null
        Start-Sleep -Milliseconds 400
        $back = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
        $backText = if ($null -ne $back) { [string][int]$back } else { '读取失败' }
        Write-MifsLog 'INFO ' ("风扇满速读回 = {0}" -f $backText)

        if ($null -ne $back -and [int]$back -ne $target) {
            # EC 接受了命令但值没变 → 本机未实现该开关，缓存结论并永久禁用按钮
            $script:FanBoostCap = $false
            Save-Caps
            Write-MifsLog 'WARN ' ("风扇满速：写 {0} 但读回 {1} → 判定本机未实现，按钮已禁用" -f $target, [int]$back)
            Refresh-All
            [System.Windows.Forms.MessageBox]::Show(
                "本机 EC 忽略风扇满速写入（写 $target 读回 $([int]$back)），已标记为未实现并禁用该按钮。`n`n结论记在：$script:CapsFile`n想重新实测请点「重测功能」。",
                'OpenMIFS', 'OK', 'Warning') | Out-Null
            return
        }
        if ($null -eq $script:FanBoostCap) { $script:FanBoostCap = $true; Save-Caps }
        Refresh-All
    }
    catch {
        Write-MifsEx '设置风扇满速失败' $_
        [System.Windows.Forms.MessageBox]::Show("设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
})
$gFan.Controls.Add($btnBoost)

$btnRecap          = New-Object System.Windows.Forms.Button
$btnRecap.Text     = '重测功能'
$btnRecap.Location = New-Object System.Drawing.Point(220, 46)
$btnRecap.Size     = New-Object System.Drawing.Size(110, 28)
$btnRecap.Font     = $FontUI
$btnRecap.Add_Click({
    if ($script:Suppress) { return }
    $script:FanBoostCap = $null
    Save-Caps
    $script:LastLoggedError = ''
    Write-MifsLog 'INFO ' '用户点击重测功能：能力缓存已清空，下次点击「风扇满速」将重新实测'
    Refresh-All
    [System.Windows.Forms.MessageBox]::Show(
        "能力缓存已清空。下次点击「风扇满速」时会重新实测（写入后读回校验）。`n`n日志：$script:LogFile",
        '重测功能', 'OK', 'Information') | Out-Null
})
$gFan.Controls.Add($btnRecap)

# ── 硬件开关
$gSw            = New-Group '硬件开关' 222 60
$chkFn          = New-Object System.Windows.Forms.CheckBox
$chkFn.Text     = 'Fn 锁'
$chkFn.Location = New-Object System.Drawing.Point(14, 24)
$chkFn.Size     = New-Object System.Drawing.Size(150, 22)
$chkFn.Font     = $FontUI
$chkFn.Add_Click({
    if ($script:Suppress) { return }
    try {
        $v = [byte]$(if ($chkFn.Checked) { 1 } else { 0 })
        Set-MifsByteLogged -Func $FNUM['FN_LOCK'] -Val $v -Label ("Fn 锁 → " + $(if ($v -eq 1) { '开' } else { '关' }))
        Refresh-All
    }
    catch {
        Write-MifsEx 'Fn 锁设置失败' $_
        [System.Windows.Forms.MessageBox]::Show("Fn 锁设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
})
$gSw.Controls.Add($chkFn)

$chkTp          = New-Object System.Windows.Forms.CheckBox
$chkTp.Text     = '触控板锁定'
$chkTp.Location = New-Object System.Drawing.Point(200, 24)
$chkTp.Size     = New-Object System.Drawing.Size(190, 22)
$chkTp.Font     = $FontUI
$chkTp.Add_Click({
    if ($script:Suppress) { return }
    try {
        $v = [byte]$(if ($chkTp.Checked) { 1 } else { 0 })
        Set-MifsByteLogged -Func $FNUM['TP_LOCK'] -Val $v -Label ("触控板锁 → " + $(if ($v -eq 1) { '锁定' } else { '正常' }))
        Refresh-All
    }
    catch {
        Write-MifsEx '触控板锁设置失败' $_
        [System.Windows.Forms.MessageBox]::Show("触控板锁设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
})
$gSw.Controls.Add($chkTp)

# ── 键盘背光
$gKbd       = New-Group '键盘背光亮度' 288 60
$script:BtnKbd = @()
$x = 12
foreach ($lv in 0..3) {
    $b          = New-Object System.Windows.Forms.Button
    $b.Text     = "$lv"
    $b.Location = New-Object System.Drawing.Point($x, 24)
    $b.Size     = New-Object System.Drawing.Size(62, 28)
    $b.Tag      = $lv
    $b.Font     = $FontUI
    $b.Add_Click({
        param($s, $e)
        if ($script:Suppress) { return }
        try {
            Set-MifsByteLogged -Func $FNUM['RGB_BRIGHT'] -Val ([byte][int]$s.Tag) -Label ("键盘背光 → 等级 " + $s.Tag) -DelayMs 150
            Refresh-All
        }
        catch {
            Write-MifsEx '背光设置失败' $_
            [System.Windows.Forms.MessageBox]::Show("背光设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        }
    })
    $gKbd.Controls.Add($b)
    $script:BtnKbd += $b
    $x += 76
}

# ── 启动与 OSD
$gOsd = New-Group '启动与 OSD 屏幕提示' 354 152

$chkStartup          = New-Object System.Windows.Forms.CheckBox
$chkStartup.Text     = '开机自启（计划任务 · 免 UAC）'
$chkStartup.Location = New-Object System.Drawing.Point(14, 22)
$chkStartup.Size     = New-Object System.Drawing.Size(232, 22)
$chkStartup.Font     = $FontUI
$chkStartup.Add_Click({
    if ($script:Suppress) { return }
    $want = $chkStartup.Checked
    Write-MifsLog 'INFO ' ("开机自启 → {0}（计划任务 OpenMIFS）" -f $(if ($want) { '开' } else { '关' }))
    $form.Cursor = 'WaitCursor'
    try {
        $code = Set-StartupTask -Enable $want
        if ($code -ne 0) {
            [System.Windows.Forms.MessageBox]::Show("开机自启设置失败（exit=$code），详见日志：`n$script:LogFile", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        }
    }
    catch {
        Write-MifsEx '设置开机自启失败' $_
        [System.Windows.Forms.MessageBox]::Show("设置开机自启失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
    finally { $form.Cursor = 'Default' }
    $script:Suppress = $true
    $null = Update-StartupStatus -Force
    $script:Suppress = $false
})
$gOsd.Controls.Add($chkStartup)

$lblOsd          = New-Object System.Windows.Forms.Label
$lblOsd.Location = New-Object System.Drawing.Point(12, 48)
$lblOsd.Size     = New-Object System.Drawing.Size(432, 20)
$lblOsd.Font     = $FontUI8
$lblOsd.Text     = 'OSD：检测中…'
$gOsd.Controls.Add($lblOsd)

# ── DPI 兼容修复（实验，可撤销）：写的就是「兼容性 → 更改高 DPI 设置」那条注册表值
$chkDpi          = New-Object System.Windows.Forms.CheckBox
$chkDpi.Text     = 'DPI 兼容修复（实验，可撤销）'
$chkDpi.Location = New-Object System.Drawing.Point(14, 110)
$chkDpi.Size     = New-Object System.Drawing.Size(240, 22)
$chkDpi.Font     = $FontUI
$chkDpi.Add_Click({
    if ($script:Suppress) { return }
    $want = $chkDpi.Checked
    $restore = {
        $script:Suppress = $true
        $null = Update-DpiCompatStatus
        $script:Suppress = $false
    }
    if (-not (Test-Path $script:DpiCompatExe)) {
        Write-MifsLog 'WARN ' ("DPI 兼容修复：找不到 {0}，官方 OSD 组件没装" -f $script:DpiCompatExe)
        [System.Windows.Forms.MessageBox]::Show("找不到 $script:DpiCompatExe，官方 OSD 组件没装，无法应用这个修复。", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        & $restore
        return
    }
    $tip = if ($want) {
        "将写入注册表（需要管理员，可随时撤销）：`r`n  HKLM\$script:DpiCompatSubKey`r`n  $script:DpiCompatExe = $script:DpiCompatFlag`r`n`r`n" +
        "作用：让 DPI 不感知的 OSD 界面进程按系统 DPI 渲染。`r`n" +
        "本机缩放不是 100%，而该进程按 96 DPI 工作 —— 这是它画不出提示的头号嫌疑。`r`n`r`n继续？"
    }
    else { "将删除上面那条注册表值，把 OSD 恢复原样。`r`n`r`n继续？" }
    $ans = [System.Windows.Forms.MessageBox]::Show($tip, $(if ($want) { '应用 DPI 兼容修复' } else { '撤销 DPI 兼容修复' }),
        'OKCancel', $(if ($want) { 'Warning' } else { 'Question' }))
    if ($ans -ne 'OK') {
        Write-MifsLog 'INFO ' '用户取消了 DPI 兼容修复操作'
        & $restore
        return
    }
    $form.Cursor = 'WaitCursor'
    try {
        Set-DpiCompat -Enable $want
        $script:DpiHint = '下一步：点「重启 OSD」（或管理员重启 OSD 服务）后按一次 Fn，看提示是否出现。'
        Refresh-All
        [System.Windows.Forms.MessageBox]::Show(
            "DPI 兼容标记已$(if ($want) { '写入' } else { '删除' })。`r`n`r`n$script:DpiHint`r`n`r`n日志：$script:LogFile",
            'DPI 兼容修复', 'OK', 'Information') | Out-Null
    }
    catch {
        Write-MifsEx '设置 DPI 兼容标记失败' $_
        [System.Windows.Forms.MessageBox]::Show("操作失败：$($_.Exception.Message)`r`n`r`n写入 HKLM 需要管理员权限。", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        & $restore
    }
    finally { $form.Cursor = 'Default' }
})
$gOsd.Controls.Add($chkDpi)

$lblDpi          = New-Object System.Windows.Forms.Label
$lblDpi.Location = New-Object System.Drawing.Point(258, 110)
$lblDpi.Size     = New-Object System.Drawing.Size(186, 22)
$lblDpi.Font     = $FontUI8
$lblDpi.ForeColor = $ColorDim
$lblDpi.Text     = '检测中…'
$gOsd.Controls.Add($lblDpi)

$btnOsdRestart          = New-Object System.Windows.Forms.Button
$btnOsdRestart.Text     = '重启 OSD'
$btnOsdRestart.Location = New-Object System.Drawing.Point(12, 74)
$btnOsdRestart.Size     = New-Object System.Drawing.Size(104, 28)
$btnOsdRestart.Font     = $FontUI
$btnOsdRestart.Add_Click({
    if ($script:Suppress) { return }
    $ans = [System.Windows.Forms.MessageBox]::Show(
        "将依次执行：`r`n  1) 停止并启动服务 $script:OsdService`r`n  2) 结束并重新启动 $script:OsdUtility`r`n`r`nOSD 界面进程只会被重启，不改动任何系统设置。继续？",
        '重启 OSD', 'OKCancel', 'Question')
    if ($ans -ne 'OK') { Write-MifsLog 'INFO ' '用户取消了 OSD 重启'; return }
    Write-MifsLog 'INFO ' '开始重启 OSD'
    $form.Cursor = 'WaitCursor'
    try {
        $result = Invoke-OsdRestart
        $script:LastOsdCheck = [datetime]::MinValue
        Refresh-All
        [System.Windows.Forms.MessageBox]::Show("$result`r`n`r`n详情见日志：`r`n$script:LogFile", '重启 OSD', 'OK', 'Information') | Out-Null
    }
    catch {
        Write-MifsEx '重启 OSD 失败' $_
        [System.Windows.Forms.MessageBox]::Show("重启 OSD 失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
    finally { $form.Cursor = 'Default' }
})
$gOsd.Controls.Add($btnOsdRestart)

$btnOsdDiag          = New-Object System.Windows.Forms.Button
$btnOsdDiag.Text     = '诊断 OSD'
$btnOsdDiag.Location = New-Object System.Drawing.Point(122, 74)
$btnOsdDiag.Size     = New-Object System.Drawing.Size(104, 28)
$btnOsdDiag.Font     = $FontUI
$btnOsdDiag.Add_Click({
    if ($script:Suppress) { return }
    Write-MifsLog 'INFO ' '开始 OSD 诊断'
    $form.Cursor = 'WaitCursor'
    try {
        $null = Invoke-OsdDiagnose
        [System.Windows.Forms.MessageBox]::Show(
            "诊断完成。结果已写入下方状态面板与日志：`r`n$script:LogFile`r`n`r`n完整证据（含事件日志）请用 exe 版：OpenMIFS.exe --diagnose",
            '诊断 OSD', 'OK', 'Information') | Out-Null
    }
    catch {
        Write-MifsEx 'OSD 诊断失败' $_
        [System.Windows.Forms.MessageBox]::Show("OSD 诊断失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
    finally { $form.Cursor = 'Default' }
})
$gOsd.Controls.Add($btnOsdDiag)

# ── 状态面板
$txtStatus            = New-Object System.Windows.Forms.TextBox
$txtStatus.Multiline  = $true
$txtStatus.ReadOnly   = $true
$txtStatus.ScrollBars = 'Vertical'
$txtStatus.WordWrap   = $false
$txtStatus.Font       = $FontMono
$txtStatus.Location   = New-Object System.Drawing.Point(12, 516)
$txtStatus.Size       = New-Object System.Drawing.Size(456, 150)
$txtStatus.BackColor  = [System.Drawing.Color]::FromArgb(250, 250, 250)
$txtStatus.Text       = ''
$form.Controls.Add($txtStatus)

# ── 底部控制
$chkAuto          = New-Object System.Windows.Forms.CheckBox
$chkAuto.Text     = '自动刷新'
$chkAuto.Location = New-Object System.Drawing.Point(14, 676)
$chkAuto.Size     = New-Object System.Drawing.Size(92, 22)
$chkAuto.Checked  = $true
$chkAuto.Font     = $FontUI8
$form.Controls.Add($chkAuto)

$cmbInterval          = New-Object System.Windows.Forms.ComboBox
$cmbInterval.Location = New-Object System.Drawing.Point(110, 675)
$cmbInterval.Size     = New-Object System.Drawing.Size(66, 22)
$cmbInterval.DropDownStyle = 'DropDownList'
$cmbInterval.Font     = $FontUI8
[void]$cmbInterval.Items.AddRange(@('2 秒', '3 秒', '5 秒', '10 秒'))
$cmbInterval.SelectedIndex = 1
$form.Controls.Add($cmbInterval)

$btnRefresh          = New-Object System.Windows.Forms.Button
$btnRefresh.Text     = '刷新'
$btnRefresh.Location = New-Object System.Drawing.Point(374, 671)
$btnRefresh.Size     = New-Object System.Drawing.Size(94, 28)
$btnRefresh.Font     = $FontUI
$btnRefresh.Add_Click({
    $script:DpiHint = ''
    try { Refresh-All } catch { Write-MifsEx '手动刷新失败' $_ }
})
$form.Controls.Add($btnRefresh)

# ──────────────────────────────── 刷新
function Refresh-All {
    $script:Suppress = $true
    try {
        $script:LastMifsError = ''
        $lines   = New-Object System.Collections.Generic.List[string]
        $missing = New-Object System.Collections.Generic.List[string]
        $anyOk   = $false
        $adminText = if ($isAdmin) { '管理员' } else { '非管理员' }

        # ── 性能模式
        $pm = Get-MifsByte -Func $FNUM['PER_MODE']
        if ($null -ne $pm) {
            $anyOk = $true
            $mi = [int]$pm
            foreach ($k in $script:BtnMode.Keys) {
                $isCur = ($ModeValue[$k] -eq $mi)
                $script:BtnMode[$k].Enabled = $true
                $script:BtnMode[$k].Text = $(if ($isCur) { "● $k" } else { "○ $k" })
                $script:BtnMode[$k].Font = $(if ($isCur) { $FontBold } else { $FontUI })
            }
            $lines.Add(("性能模式     : {0}" -f $(if ($ModeLabel.ContainsKey($mi)) { $ModeLabel[$mi] } else { "未知($mi)" })))
        }
        else {
            foreach ($k in $script:BtnMode.Keys) {
                $script:BtnMode[$k].Enabled = $false
                $script:BtnMode[$k].Text = "$k（未实现）"
                $script:BtnMode[$k].Font = $FontUI
            }
            $missing.Add('性能模式')
            $lines.Add('性能模式     : 未实现')
        }

        # ── 风扇转速
        $fan = Get-MifsFan
        if ($fan) {
            $anyOk = $true
            $lblFan.ForeColor = [System.Drawing.SystemColors]::ControlText
            $lblFan.Text = "风扇1  $($fan[0]) RPM      风扇2  $($fan[1]) RPM" + $(if ($fan[2] -gt 0) { "      风扇3  $($fan[2]) RPM" } else { '' })
            $lines.Add(("风扇1        : {0} RPM" -f $fan[0]))
            $lines.Add(("风扇2        : {0} RPM" -f $fan[1]))
        }
        else {
            $lblFan.ForeColor = $ColorDim
            $lblFan.Text = '风扇转速：本机未实现'
            $missing.Add('风扇转速')
            $lines.Add('风扇转速     : 未实现')
        }

        # ── 风扇满速：可写开关，靠能力缓存 + 写后读回判定
        $boostReadable = $false
        $mxs = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
        if ($null -ne $mxs) {
            $boostReadable = $true
            $script:FanBoostOn = ([int]$mxs -eq 1)
        }
        $boostText = if ($script:FanBoostOn) { '开' } else { '关' }

        if ($null -eq $script:FanBoostCap) {
            # 未检测：允许点一次，点后按读回结果定性
            $script:FanBoostUsable = $boostReadable
            $btnBoost.Enabled = $boostReadable
            $btnBoost.Text = $(if ($boostReadable) { "风扇满速：$boostText（未检测）" } else { '风扇满速：未实现' })
            $btnBoost.Font = $FontUI
            $lines.Add("风扇满速     : " + $(if ($boostReadable) { "$boostText（未检测，点击后自动判定）" } else { '未实现' }))
        }
        elseif ($script:FanBoostCap -and $boostReadable) {
            $script:FanBoostUsable = $true
            $btnBoost.Enabled = $true
            $btnBoost.Text = "风扇满速：$boostText"
            $btnBoost.Font = $(if ($script:FanBoostOn) { $FontBold } else { $FontUI })
            $lines.Add("风扇满速     : $boostText")
        }
        else {
            $script:FanBoostUsable = $false
            $btnBoost.Enabled = $false
            $btnBoost.Text = '风扇满速：未实现'
            $btnBoost.Font = $FontUI
            $missing.Add('风扇满速')
            $lines.Add('风扇满速     : 未实现（EC 忽略写入）')
        }

        # ── Fn 锁
        $fnl = Get-MifsByte -Func $FNUM['FN_LOCK']
        if ($null -ne $fnl) {
            $chkFn.Enabled = $true
            $chkFn.Text = 'Fn 锁'
            $chkFn.Checked = ([int]$fnl -eq 1)
            $lines.Add(("Fn 锁        : {0}" -f $(if ($chkFn.Checked) { '开' } else { '关' })))
        }
        else {
            $chkFn.Enabled = $false
            $chkFn.Text = 'Fn 锁（未实现）'
            $missing.Add('Fn 锁')
            $lines.Add('Fn 锁        : 未实现')
        }

        # ── 触控板锁
        $tpl = Get-MifsByte -Func $FNUM['TP_LOCK']
        if ($null -ne $tpl) {
            $chkTp.Enabled = $true
            $chkTp.Text = '触控板锁定'
            $chkTp.Checked = ([int]$tpl -eq 1)
            $lines.Add(("触控板锁     : {0}" -f $(if ($chkTp.Checked) { '已锁定' } else { '正常' })))
        }
        else {
            $chkTp.Enabled = $false
            $chkTp.Text = '触控板锁（未实现）'
            $missing.Add('触控板锁')
            $lines.Add('触控板锁     : 未实现')
        }

        # ── 键盘背光
        $kbd = Get-MifsByte -Func $FNUM['RGB_BRIGHT']
        $gKbd.Text = $(if ($null -ne $kbd) { '键盘背光亮度' } else { '键盘背光亮度（未实现）' })
        foreach ($b in $script:BtnKbd) {
            if ($null -eq $kbd) {
                $b.Enabled = $false
                $b.Text = '—'
                $b.Font = $FontUI
                continue
            }
            $b.Enabled = $true
            $b.Text = [string][int]$b.Tag
            $isCur = ([int]$kbd -eq [int]$b.Tag)
            $b.Font = $(if ($isCur) { $FontBold } else { $FontUI })
        }
        if ($null -ne $kbd) { $lines.Add(("键盘背光     : 等级 {0}" -f $kbd)) }
        else {
            $missing.Add('键盘背光')
            $lines.Add('键盘背光     : 未实现')
        }

        # ── 供电 / 温度 / 功率
        $ac = Get-MifsByte -Func $FNUM['AC_TYPE']
        if ($null -ne $ac) {
            $acText = if ([int]$ac -eq 1) { '外接电源' } elseif ([int]$ac -eq 0) { '电池供电' } else { "原始值 $ac" }
            $lines.Add(("供电         : {0}" -f $acText))
        }

        $ct = Get-MifsByte -Func $FNUM['CPU_TEMP']
        $ctOk = ($null -ne $ct -and [int]$ct -gt 0)
        $lines.Add(("CPU 温度     : {0}" -f $(if ($ctOk) { "$ct ℃" } else { '未实现' })))
        if (-not $ctOk) { $missing.Add('CPU 温度') }

        $cp = Get-MifsByte -Func $FNUM['CPU_POWER']
        $cpOk = ($null -ne $cp -and [int]$cp -gt 0)
        $lines.Add(("CPU 功率     : {0}" -f $(if ($cpOk) { "$cp W" } else { '未实现' })))
        if (-not $cpOk) { $missing.Add('CPU 功率') }

        # ── OSD（8 秒最多查一次）
        $null = Update-OsdStatus
        $lines.Add(("OSD          : {0}" -f $script:OsdDetail))

        # ── 开机自启（20 秒最多查一次）
        $null = Update-StartupStatus
        $lines.Add(("开机自启     : {0}" -f $(if ($chkStartup.Checked) { '已启用（计划任务 OpenMIFS）' } else { '未启用' })))

        # ── DPI 兼容修复（注册表读取很轻，每次刷新都同步；Suppress 已是 $true，不会触发 Click）
        $dpiText = Update-DpiCompatStatus
        $lines.Add(("DPI 兼容修复 : {0}" -f $dpiText))

        # ── 顶部状态条（两行）
        if ($anyOk) {
            $lblHeader.ForeColor = $ColorOk
            $missingText = if ($missing.Count -eq 0) { '无，全部功能可用' }
                           elseif ($missing.Count -le 5) { $missing -join '、' }
                           else { (($missing | Select-Object -First 5) -join '、') + '…' }
            $lblHeader.Text = "接口正常 · $adminText · 调用方式 $script:TargetHow" + "`r`n" + "本机未实现：$missingText"
        }
        else {
            $lblHeader.ForeColor = $ColorWarn
            $lblHeader.Text = "接口不可用 · $adminText · 调用方式 $script:TargetHow" + "`r`n" + "请以管理员身份运行，或确认本机 BIOS 暴露了 MIFS 接口"
            $errText = if ($script:LastMifsError) { $script:LastMifsError } else { '接口无响应' }
            # 自动刷新不写日志，只有错误文本变化时记一次
            if ($script:LastLoggedError -ne $errText) {
                $script:LastLoggedError = $errText
                Write-MifsLog 'ERROR' ("MIFS 接口不可用：{0}（{1}，调用方式 {2}）" -f $errText, $adminText, $script:TargetHow)
            }
        }

        $lines.Add('')
        if ($script:DpiHint) { $lines.Add(("提示         : {0}" -f $script:DpiHint)); $lines.Add('') }
        $lines.Add(("日志文件     : {0}" -f $script:LogFile))
        $lines.Add('提示：MIFS 接口不提供电池充电阈值，无法用软件限制充电到 80%。')
        $lines.Add(("最后刷新     : {0}" -f (Get-Date).ToString('HH:mm:ss')))
        $txtStatus.Text = ($lines -join "`r`n")
    }
    finally { $script:Suppress = $false }
}

# ──────────────────────────────── 自动刷新
$timer          = New-Object System.Windows.Forms.Timer
$timer.Interval = 3000
$timer.Add_Tick({
    if (-not $chkAuto.Checked) { return }
    try { Refresh-All } catch { Write-MifsEx '自动刷新失败' $_ }
})
$timer.Start()

$cmbInterval.Add_SelectedIndexChanged({
    if ($script:Suppress) { return }
    $sec = switch ($cmbInterval.SelectedIndex) { 0 { 2 } 1 { 3 } 2 { 5 } 3 { 10 } default { 3 } }
    $timer.Interval = $sec * 1000
})

$form.Add_Shown({
    try { Refresh-All } catch { Write-MifsEx '首次刷新失败' $_ }
})

# ──────────────────────────────── 启动
Load-Caps
$script:InterfaceOk = ($null -ne (Get-MifsByte -Func $FNUM['PER_MODE']))
Write-MifsLog 'INFO ' ('================ OpenMIFS GUI {0}{1} 启动 ================' -f $script:AppVersion, $(if ($SmokeTest) { '（SmokeTest）' } else { '' }))
Write-MifsLog 'INFO ' ("程序路径     : {0}" -f $PSCommandPath)
Write-MifsLog 'INFO ' ("用户 / 管理员: {0}\{1} / {2}" -f $env:USERDOMAIN, $env:USERNAME, $(if ($isAdmin) { '是' } else { '否' }))
Write-MifsLog 'INFO ' ("系统         : {0}  PowerShell {1}" -f [Environment]::OSVersion.VersionString, $PSVersionTable.PSVersion)
Write-MifsLog 'INFO ' ("数据目录     : {0}" -f $script:LogDir)
Write-MifsLog 'INFO ' ("MIFS 接口    : {0}" -f $(if ($script:InterfaceOk) { '可用' } else { '不可用（未提权或本机 BIOS 未暴露接口）' }))

if ($SmokeTest) {
    Write-Host 'SmokeTest: 构建界面并执行一次刷新…'
    $form.CreateControl()
    Refresh-All
    Write-Host '  ✅ 界面构建与刷新流程未抛异常'
    Write-Host ("  状态条底边 {0} px / 性能模式分组顶边 {1} px（前者必须 < 后者）" -f $lblHeader.Bottom, $gMode.Top)
    Write-Host ("  顶部状态条 : {0}" -f ($lblHeader.Text -replace "`r`n", ' ｜ '))
    Write-Host ("  风扇标签   : {0}" -f $lblFan.Text)
    Write-Host ("  性能按钮   : {0}" -f (($script:BtnMode.Keys | ForEach-Object { "$($script:BtnMode[$_].Text)[$($script:BtnMode[$_].Enabled)]" }) -join '  '))
    Write-Host ("  背光按钮   : {0}  分组标题：{1}" -f (($script:BtnKbd | ForEach-Object { "$($_.Text)[$($_.Enabled)]" }) -join '  '), $gKbd.Text)
    Write-Host ("  风扇满速   : {0}  可点={1}  能力={2}" -f $btnBoost.Text, $btnBoost.Enabled, (Get-CapText))
    Write-Host ("  OSD        : {0}" -f $lblOsd.Text)
    Write-Host ("  开机自启   : 勾选={0}  文字={1}" -f $chkStartup.Checked, $chkStartup.Text)
    Write-Host ("  DPI 修复   : 勾选={0}  可点={1}  文字={2}" -f $chkDpi.Checked, $chkDpi.Enabled, $lblDpi.Text)
    Write-Host ("  OSD分组底边: {0} px / 面板顶边 {1} px / 面板底边 {2} px" -f ($gOsd.Top + $gOsd.Height), $txtStatus.Top, $txtStatus.Bottom)
    Write-Host ("  底部行底边 : {0} px / 客户区高 {1} px" -f $btnRefresh.Bottom, $form.ClientSize.Height)
    Write-Host ("  状态面板行数: {0}" -f ($txtStatus.Text -split "`r`n").Count)
    Write-MifsLog 'INFO ' '================ OpenMIFS GUI 退出（SmokeTest）================'
    $form.Dispose()
    exit 0
}

[void]$form.ShowDialog()
$timer.Stop()
$form.Dispose()
Write-MifsLog 'INFO ' '================ OpenMIFS GUI 退出 ================'
