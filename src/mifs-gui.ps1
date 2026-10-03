<#
  OpenMIFS — 同方 MIFS (MiInterface) 图形控制台
  ==================================================================
  不依赖任何官方控制中心组件。纯 PowerShell + WinForms，单文件、零依赖。

  工作原理与功能号详见 src/mifs.ps1 顶部注释与 docs/PROTOCOL.md
  ACPI 设备 : ACPI\PNP0C14\MIFS      WMI 类 : root\wmi:MICommonInterface
  ACPI GUID : {B60BFB48-3E5B-49E4-A0E9-8CFFE1B3434B}

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

# ──────────────────────────────── 自提权
if (-not $SmokeTest) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        try {
            Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
            )
            exit 0
        }
        catch {
            [System.Windows.Forms.MessageBox]::Show("需要管理员权限才能访问 MIFS 接口。`n`n$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Error') | Out-Null
            exit 1
        }
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

function Set-MifsByte {
    param([int]$Func, [byte]$Val)
    $b = New-Object byte[] 1
    $b[0] = $Val
    Invoke-Mifs -Type $SET -Func $Func -SetPayload $b | Out-Null
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
$form.ClientSize      = New-Object System.Drawing.Size(480, 586)
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

# ── 顶部状态条
$lblHeader           = New-Object System.Windows.Forms.Label
$lblHeader.Location  = New-Object System.Drawing.Point(14, 8)
$lblHeader.Size      = New-Object System.Drawing.Size(452, 34)
$lblHeader.Font      = $FontUI8
$lblHeader.Text      = '正在检测接口…'
$form.Controls.Add($lblHeader)

# ── 性能模式
$gMode = New-Group '性能模式' 48 70
$script:BtnMode = @{}
$x = 12
foreach ($mn in $ModeOrder) {
    $b          = New-Object System.Windows.Forms.Button
    $b.Text     = $mn
    $b.Location = New-Object System.Drawing.Point($x, 26)
    $b.Size     = New-Object System.Drawing.Size(140, 30)
    $b.Tag      = $mn
    $b.Font     = $FontUI
    $b.Add_Click({
        param($s, $e)
        if ($script:Suppress) { return }
        $name = $s.Tag
        try {
            Set-MifsByte -Func $FNUM['PER_MODE'] -Val ([byte]$ModeValue[$name])
            Start-Sleep -Milliseconds 250
            Refresh-All
        }
        catch {
            [System.Windows.Forms.MessageBox]::Show("切换失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
        }
    })
    $gMode.Controls.Add($b)
    $script:BtnMode[$mn] = $b
    $x += 148
}

# ── 风扇
$gFan           = New-Group '风扇' 124 84
$lblFan         = New-Object System.Windows.Forms.Label
$lblFan.Location = New-Object System.Drawing.Point(12, 22)
$lblFan.Size     = New-Object System.Drawing.Size(432, 20)
$lblFan.Font     = $FontUI8
$lblFan.Text     = '读取中…'
$gFan.Controls.Add($lblFan)

$script:FanBoostOn = $false
$btnBoost          = New-Object System.Windows.Forms.Button
$btnBoost.Location = New-Object System.Drawing.Point(12, 46)
$btnBoost.Size     = New-Object System.Drawing.Size(180, 28)
$btnBoost.Font     = $FontUI
$btnBoost.Text     = '风扇满速：关'
$btnBoost.Add_Click({
    if ($script:Suppress) { return }
    $target = if ($script:FanBoostOn) { 0 } else { 1 }
    try {
        $b = New-Object byte[] 2
        $b[0] = 0
        $b[1] = [byte]$target
        Invoke-Mifs -Type $SET -Func $FNUM['MAX_FAN_SWITCH'] -SetPayload $b | Out-Null
        Start-Sleep -Milliseconds 400
        Refresh-All
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show("设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null
    }
})
$gFan.Controls.Add($btnBoost)

# ── 硬件开关
$gSw            = New-Group '硬件开关' 214 62
$chkFn          = New-Object System.Windows.Forms.CheckBox
$chkFn.Text     = 'Fn 锁'
$chkFn.Location = New-Object System.Drawing.Point(14, 24)
$chkFn.Size     = New-Object System.Drawing.Size(150, 22)
$chkFn.Font     = $FontUI
$chkFn.Add_Click({
    if ($script:Suppress) { return }
    try { Set-MifsByte -Func $FNUM['FN_LOCK'] -Val ([byte]$(if ($chkFn.Checked) { 1 } else { 0 })); Start-Sleep -Milliseconds 200; Refresh-All }
    catch { [System.Windows.Forms.MessageBox]::Show("Fn 锁设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null }
})
$gSw.Controls.Add($chkFn)

$chkTp          = New-Object System.Windows.Forms.CheckBox
$chkTp.Text     = '触控板锁定'
$chkTp.Location = New-Object System.Drawing.Point(200, 24)
$chkTp.Size     = New-Object System.Drawing.Size(190, 22)
$chkTp.Font     = $FontUI
$chkTp.Add_Click({
    if ($script:Suppress) { return }
    try { Set-MifsByte -Func $FNUM['TP_LOCK'] -Val ([byte]$(if ($chkTp.Checked) { 1 } else { 0 })); Start-Sleep -Milliseconds 200; Refresh-All }
    catch { [System.Windows.Forms.MessageBox]::Show("触控板锁设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null }
})
$gSw.Controls.Add($chkTp)

# ── 键盘背光
$gKbd       = New-Group '键盘背光亮度' 282 62
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
        try { Set-MifsByte -Func $FNUM['RGB_BRIGHT'] -Val ([byte][int]$s.Tag); Start-Sleep -Milliseconds 150; Refresh-All }
        catch { [System.Windows.Forms.MessageBox]::Show("背光设置失败：$($_.Exception.Message)", 'OpenMIFS', 'OK', 'Warning') | Out-Null }
    })
    $gKbd.Controls.Add($b)
    $script:BtnKbd += $b
    $x += 76
}

# ── 状态面板
$txtStatus            = New-Object System.Windows.Forms.TextBox
$txtStatus.Multiline  = $true
$txtStatus.ReadOnly   = $true
$txtStatus.ScrollBars = 'Vertical'
$txtStatus.WordWrap   = $false
$txtStatus.Font       = $FontMono
$txtStatus.Location   = New-Object System.Drawing.Point(12, 352)
$txtStatus.Size       = New-Object System.Drawing.Size(456, 184)
$txtStatus.BackColor  = [System.Drawing.Color]::FromArgb(250, 250, 250)
$txtStatus.Text       = ''
$form.Controls.Add($txtStatus)

# ── 底部控制
$chkAuto          = New-Object System.Windows.Forms.CheckBox
$chkAuto.Text     = '自动刷新'
$chkAuto.Location = New-Object System.Drawing.Point(14, 548)
$chkAuto.Size     = New-Object System.Drawing.Size(92, 22)
$chkAuto.Checked  = $true
$chkAuto.Font     = $FontUI8
$form.Controls.Add($chkAuto)

$cmbInterval          = New-Object System.Windows.Forms.ComboBox
$cmbInterval.Location = New-Object System.Drawing.Point(110, 547)
$cmbInterval.Size     = New-Object System.Drawing.Size(66, 22)
$cmbInterval.DropDownStyle = 'DropDownList'
$cmbInterval.Font     = $FontUI8
[void]$cmbInterval.Items.AddRange(@('2 秒', '3 秒', '5 秒', '10 秒'))
$cmbInterval.SelectedIndex = 1
$form.Controls.Add($cmbInterval)

$btnRefresh          = New-Object System.Windows.Forms.Button
$btnRefresh.Text     = '刷新'
$btnRefresh.Location = New-Object System.Drawing.Point(374, 543)
$btnRefresh.Size     = New-Object System.Drawing.Size(94, 28)
$btnRefresh.Font     = $FontUI
$btnRefresh.Add_Click({ Refresh-All })
$form.Controls.Add($btnRefresh)

# ──────────────────────────────── 刷新
function Refresh-All {
    $script:Suppress = $true
    $lines = New-Object System.Collections.Generic.List[string]
    $anyOk = $false

    # 性能模式
    $pm = Get-MifsByte -Func $FNUM['PER_MODE']
    if ($null -ne $pm) {
        $anyOk = $true
        $mi = [int]$pm
        foreach ($k in $script:BtnMode.Keys) {
            $isCur = ($ModeValue[$k] -eq $mi)
            $script:BtnMode[$k].Text = $(if ($isCur) { "● $k" } else { "○ $k" })
            $script:BtnMode[$k].Font = $(if ($isCur) { $FontBold } else { $FontUI })
        }
        $lines.Add(("性能模式     : {0}" -f $(if ($ModeLabel.ContainsKey($mi)) { $ModeLabel[$mi] } else { "未知($mi)" })))
    }
    else {
        foreach ($k in $script:BtnMode.Keys) { $script:BtnMode[$k].Enabled = $false; $script:BtnMode[$k].Text = "$k（未实现）" }
        $lines.Add('性能模式     : 未实现')
    }

    # 风扇
    $fan = Get-MifsFan
    if ($fan) {
        $anyOk = $true
        $lblFan.Text = "风扇1  $($fan[0]) RPM      风扇2  $($fan[1]) RPM" + $(if ($fan[2] -gt 0) { "      风扇3  $($fan[2]) RPM" } else { '' })
        $lines.Add(("风扇1        : {0} RPM" -f $fan[0]))
        $lines.Add(("风扇2        : {0} RPM" -f $fan[1]))
    }
    else {
        $lblFan.Text = '风扇：本机未实现'
        $lines.Add('风扇转速     : 未实现')
    }

    # 风扇满速
    $mxs = Get-MifsByte -Func $FNUM['MAX_FAN_SWITCH']
    if ($null -ne $mxs) {
        $script:FanBoostOn = ([int]$mxs -eq 1)
        $btnBoost.Text = '风扇满速：' + $(if ($script:FanBoostOn) { '开' } else { '关' })
        $btnBoost.Font = $(if ($script:FanBoostOn) { $FontBold } else { $FontUI })
        $lines.Add(("风扇满速     : {0}" -f $(if ($script:FanBoostOn) { '开' } else { '关' })))
    }
    else {
        $btnBoost.Enabled = $false
        $btnBoost.Text = '风扇满速：未实现'
        $lines.Add('风扇满速     : 未实现')
    }

    # Fn 锁 / 触控板锁
    $fnl = Get-MifsByte -Func $FNUM['FN_LOCK']
    if ($null -ne $fnl) {
        $chkFn.Checked = ([int]$fnl -eq 1)
        $lines.Add(("Fn 锁        : {0}" -f $(if ($chkFn.Checked) { '开' } else { '关' })))
    }
    else { $chkFn.Enabled = $false; $chkFn.Text = 'Fn 锁（未实现）'; $lines.Add('Fn 锁        : 未实现') }

    $tpl = Get-MifsByte -Func $FNUM['TP_LOCK']
    if ($null -ne $tpl) {
        $chkTp.Checked = ([int]$tpl -eq 1)
        $lines.Add(("触控板锁     : {0}" -f $(if ($chkTp.Checked) { '已锁定' } else { '正常' })))
    }
    else { $chkTp.Enabled = $false; $chkTp.Text = '触控板锁（未实现）'; $lines.Add('触控板锁     : 未实现') }

    # 键盘背光
    $kbd = Get-MifsByte -Func $FNUM['RGB_BRIGHT']
    foreach ($b in $script:BtnKbd) {
        if ($null -eq $kbd) { $b.Enabled = $false; continue }
        $isCur = ([int]$kbd -eq [int]$b.Tag)
        $b.Font = $(if ($isCur) { $FontBold } else { $FontUI })
    }
    if ($null -ne $kbd) { $lines.Add(("键盘背光     : 等级 {0}" -f $kbd)) }
    else { $lines.Add('键盘背光     : 未实现') }

    # 供电 / 温度 / 功率
    $ac = Get-MifsByte -Func $FNUM['AC_TYPE']
    if ($null -ne $ac) {
        $acText = if ([int]$ac -eq 1) { '外接电源' } elseif ([int]$ac -eq 0) { '电池供电' } else { "原始值 $ac" }
        $lines.Add(("供电         : {0}" -f $acText))
    }
    $ct = Get-MifsByte -Func $FNUM['CPU_TEMP']
    $lines.Add(("CPU 温度     : {0}" -f $(if ($null -ne $ct -and [int]$ct -gt 0) { "$ct ℃" } else { '未实现' })))
    $cp = Get-MifsByte -Func $FNUM['CPU_POWER']
    $lines.Add(("CPU 功率     : {0}" -f $(if ($null -ne $cp -and [int]$cp -gt 0) { "$cp W" } else { '未实现' })))

    # 顶部状态条
    $adminText = if (([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { '管理员' } else { '非管理员' }
    if ($anyOk) {
        $lblHeader.ForeColor = $ColorOk
        $lblHeader.Text = "接口正常 · $adminText · 调用方式 $script:TargetHow" + "`r`n" + "本机未实现：CPU 温度/功率、GPU 模式、RGB 模式与颜色、风扇满速"
    }
    else {
        $lblHeader.ForeColor = $ColorWarn
        $lblHeader.Text = "接口不可用 · $adminText · 调用方式 $script:TargetHow" + "`r`n" + "请以管理员身份运行，或确认本机 BIOS 暴露了 MIFS 接口"
    }

    $lines.Add('')
    $lines.Add('提示：MIFS 接口不提供电池充电阈值，无法用软件限制充电到 80%。')
    $lines.Add(("最后刷新     : {0}" -f (Get-Date).ToString('HH:mm:ss')))
    $txtStatus.Text = ($lines -join "`r`n")

    $script:Suppress = $false
}

# ──────────────────────────────── 自动刷新
$timer          = New-Object System.Windows.Forms.Timer
$timer.Interval = 3000
$timer.Add_Tick({
    if ($chkAuto.Checked) { Refresh-All }
})
$timer.Start()

$cmbInterval.Add_SelectedIndexChanged({
    if ($script:Suppress) { return }
    $sec = switch ($cmbInterval.SelectedIndex) { 0 { 2 } 1 { 3 } 2 { 5 } 3 { 10 } default { 3 } }
    $timer.Interval = $sec * 1000
})

$form.Add_Shown({ Refresh-All })

# ──────────────────────────────── 启动
if ($SmokeTest) {
    Write-Host 'SmokeTest: 构建界面并执行一次刷新…'
    $form.CreateControl()
    Refresh-All
    Write-Host '  ✅ 界面构建与刷新流程未抛异常'
    Write-Host ("  顶部状态条 : {0}" -f ($lblHeader.Text -replace "`r`n", ' | '))
    Write-Host ("  风扇标签   : {0}" -f $lblFan.Text)
    Write-Host ("  性能按钮   : {0}" -f (($script:BtnMode.Keys | ForEach-Object { $script:BtnMode[$_].Text }) -join '  '))
    Write-Host ("  状态面板行数: {0}" -f ($txtStatus.Text -split "`r`n").Count)
    $form.Dispose()
    exit 0
}

[void]$form.ShowDialog()
$timer.Stop()
$form.Dispose()
