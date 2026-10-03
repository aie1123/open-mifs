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
    .\mifs.ps1 probe               诊断调用链（先跑这个）
    .\mifs.ps1 test                逐个探测驱动清单里的功能号
    .\mifs.ps1 scan [最大号]       扫描 0..N（默认 63）找出所有可用功能号
    .\mifs.ps1 bench               依次设 0/1/2/3 加满负载，实测各档功耗高低（约 2 分钟）
    .\mifs.ps1 mode low            切模式：low / balanced / performance（也接受 低功耗/均衡/性能）
    .\mifs.ps1 fanboost on         风扇满速开关（本机未实现，会提示）
    .\mifs.ps1 kbd 0               键盘背光 0~3
    .\mifs.ps1 raw 19              对指定功能号发 GET，打印原始字节

  安全
    status / probe / test / scan / raw  只发 GET(250)，纯读，不改任何状态。
    mode / fanboost / kbd               是 SET(251)，会写 EC 寄存器，均为官方定义的可逆开关。
    不要在未确认含义的情况下对未知功能号发 SET。
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('status', 'probe', 'test', 'scan', 'bench', 'mode', 'fanboost', 'kbd', 'raw')]
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
        Write-Host ("供电状态   : {0}" -f $(if ([int]$ac -eq 1) { '外接电源' } elseif ([int]$ac -eq 0) { '电池供电' } else { "原始值 $ac" }))
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
    Write-Host ''
}

# ──────────────────────────────── 主流程
try {
    switch ($Action) {
        'status' { Show-Status }
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
            if ($null -eq $before -or $null -eq $after) {
                Write-Host '  ⚠️ 该功能号本机不可读，风扇满速很可能未实现。' -ForegroundColor Yellow
            }
            elseif ([int]$before -eq [int]$after) {
                Write-Host '  ⚠️ 值没有变化 —— EC 忽略了这次写入，本机未实现该开关。' -ForegroundColor Yellow
            }
            else {
                Write-Host '  ✅ 值已改变，开关生效。' -ForegroundColor Green
            }
        }
        'kbd'    {
            if (-not $Value) { throw 'kbd 需要 0~3' }
            Set-MifsByte -Func $FNUM['RGB_BRIGHT'] -Val ([byte][int]$Value)
            Write-Host ("已发送: 键盘背光 {0}" -f $Value) -ForegroundColor Green
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
