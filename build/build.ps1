<#
  build.ps1 — OpenMIFS 构建脚本
  ------------------------------------------------------------------
  产物：dist\OpenMIFS.exe
        · 单个 exe，无外部依赖
        · 只依赖系统自带的 .NET Framework 4.8（Win10 1809+ / Win11 内置）
        · 已内嵌 requireAdministrator 清单（双击即弹 UAC）
        · 已内嵌应用图标

  工具链：使用 Windows 自带的 C# 编译器
          %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
          不需要 .NET SDK、不需要 Visual Studio、不需要联网。

  用法
    powershell -ExecutionPolicy Bypass -File build\build.ps1
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -Icon "C:\my.ico"     # 换图标（自用）
    powershell -ExecutionPolicy Bypass -File build\build.ps1 -Icon $null           # 不要图标
#>
[CmdletBinding()]
param(
    [string]$Icon    = '',                                  # 空 = 用 assets\icon.ico
    [string]$OutDir  = '',
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
# csc 的错误信息按控制台代码页输出，统一成 UTF-8 以免中文乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ── 路径解析（$PSScriptRoot 在 param 默认值里可能为空）
$repoRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }
if (-not $Icon)   { $Icon   = Join-Path $repoRoot 'assets\icon.ico' }

$source   = Join-Path $repoRoot 'src\csharp\OpenMIFS.cs'
$manifest = Join-Path $repoRoot 'src\csharp\app.manifest'
$outExe   = Join-Path $OutDir  'OpenMIFS.exe'

function Say($msg, $color = 'Gray') { if (-not $Quiet) { Write-Host $msg -ForegroundColor $color } }

Say ''
Say '===== OpenMIFS 构建 =====' 'Cyan'

# ── 1. 找编译器
$cscCandidates = @(
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "找不到 C# 编译器。请确认系统已启用 .NET Framework 4.x（Windows 10/11 默认自带）。`n尝试过：`n  $($cscCandidates -join "`n  ")"
}
$cscVer = (& $csc /? 2>&1 | Select-Object -First 1)
Say "编译器  : $csc"
Say "版本    : $cscVer"

# ── 2. 检查输入
foreach ($f in @($source, $manifest)) {
    if (-not (Test-Path $f)) { throw "缺少文件：$f" }
}
# 源码必须带 UTF-8 BOM，否则 csc 会按系统代码页解析，中文全乱
$bytes = [System.IO.File]::ReadAllBytes($source)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
if (-not $hasBom) {
    Say '源码缺少 UTF-8 BOM，自动补上（否则中文会乱码）' 'Yellow'
    $text = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($source, $text, (New-Object System.Text.UTF8Encoding($true)))
}

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
if (Test-Path $outExe) { Remove-Item $outExe -Force }

# ── 3. 组装编译参数
$refs = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Management.dll')
$argList = New-Object System.Collections.Generic.List[string]
$argList.Add('/nologo')
$argList.Add('/target:winexe')          # GUI 程序，不弹控制台窗口
$argList.Add('/platform:anycpu')
$argList.Add('/optimize+')
$argList.Add('/codepage:65001')
$argList.Add('/utf8output')         # 源码按 UTF-8 解析
$argList.Add('/warn:4')
$argList.Add('/win32manifest:' + $manifest)
foreach ($r in $refs) { $argList.Add('/reference:' + $r) }

$iconUsed = '未使用'
if ($Icon -and (Test-Path $Icon)) {
    $argList.Add('/win32icon:' + $Icon)
    $iconUsed = "$Icon ($((Get-Item $Icon).Length) 字节)"
}
elseif ($Icon) {
    Say "警告：图标不存在，跳过 —— $Icon" 'Yellow'
    $iconUsed = '未使用（指定路径不存在）'
}

$argList.Add('/out:' + $outExe)
$argList.Add($source)

Say "源码    : $source"
Say "清单    : $manifest"
Say "图标    : $iconUsed"
Say "输出    : $outExe"
Say ''
Say '编译中…' 'Yellow'

# ── 4. 编译
$compileOut = & $csc $argList.ToArray() 2>&1
$code = $LASTEXITCODE
if ($compileOut) { $compileOut | ForEach-Object { Say "  $_" $(if ($code -eq 0) { 'DarkGray' } else { 'Red' }) } }
if ($code -ne 0 -or -not (Test-Path $outExe)) {
    throw "编译失败（csc 退出码 $code）"
}

# ── 5. 结果
$item = Get-Item $outExe
$ver  = $item.VersionInfo
$hash = (Get-FileHash $outExe -Algorithm SHA256).Hash

Say ''
Say '===== 构建成功 =====' 'Green'
Say ("文件      : {0}" -f $item.FullName)
Say ("大小      : {0:N0} 字节 ({1:N1} KB)" -f $item.Length, ($item.Length / 1KB))
Say ("SHA256    : {0}" -f $hash)
Say ("产品版本  : {0}" -f $ver.ProductVersion)
Say ("文件版本  : {0}" -f $ver.FileVersion)
Say ("产品名    : {0}" -f $ver.ProductName)
Say ''
Say '该 exe 为单文件，无外部依赖，只要求系统自带 .NET Framework 4.8。' 'DarkGray'
Say '双击运行会弹出 UAC（清单里声明了 requireAdministrator）。' 'DarkGray'
Say ''

# 输出机器可读的一行，便于 CI 抓取
Write-Output ("OPENMIFS_BUILD_OK path={0} size={1} sha256={2}" -f $item.FullName, $item.Length, $hash)
