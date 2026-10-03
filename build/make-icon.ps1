<#
  make-icon.ps1 — 生成 OpenMIFS 应用图标（原创设计）
  ------------------------------------------------------------------
  为什么不用官方图标：
    MECHREVO 的紫色六边形 + 闪电 S 是他们的注册商标。把它作为第三方工具的
    图标提交到公开 MIT 仓库，涉及商标与著作权风险，也可能被误认为官方出品。
    所以这里画一个「同一视觉家族、但图形完全不同」的原创图标：
      · 相同的圆角六边形轮廓（控制中心类工具的通用语言）
      · 品牌紫 #5E5AE4 起手，向右下渐变为青色（官方是纯色，这里明显不同）
      · 图形换成「三根递升档位柱」，表达性能档位，与该品牌的闪电 S 无关联

  如果你想在本地自用官方图标（不提交到仓库）：
    .\build\build.ps1 -Icon "C:\path\to\official.ico"

  用法
    powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
    输出：assets\icon.ico（多尺寸 16/24/32/48/64/128/256）
#>
[CmdletBinding()]
param(
    [string]$OutPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not $OutPath) {
    $repoRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { (Get-Location).Path }
    $OutPath = Join-Path $repoRoot 'assets\icon.ico'
}

# ── 配色（品牌紫 → 青，均有别于官方纯色）
$ColFrom = [System.Drawing.Color]::FromArgb(255, 94, 90, 228)    # #5E5AE4 同族
$ColTo   = [System.Drawing.Color]::FromArgb(255, 34, 211, 238)   # #22D3EE

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $R * 2
    if ($d -gt $W) { $d = $W }
    if ($d -gt $H) { $d = $H }
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-HexPath {
    param([single]$Size, [single]$Pad, [single]$CornerCut = 0.30)
    # 尖顶六边形，再按 CornerCut 比例切角，得到近似圆角的十二边形
    $cx = $Size / 2
    $cy = $Size / 2
    $r  = ($Size / 2) - $Pad
    $v = @()
    for ($i = 0; $i -lt 6; $i++) {
        $a = [Math]::PI / 3 * $i - [Math]::PI / 2
        $v += , @(($cx + $r * [Math]::Cos($a)), ($cy + $r * [Math]::Sin($a)))
    }
    $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
    for ($i = 0; $i -lt 6; $i++) {
        $prev = $v[($i + 5) % 6]
        $cur  = $v[$i]
        $next = $v[($i + 1) % 6]
        $p1 = New-Object System.Drawing.PointF(
            ([single]($cur[0] + ($prev[0] - $cur[0]) * $CornerCut)),
            ([single]($cur[1] + ($prev[1] - $cur[1]) * $CornerCut)))
        $p2 = New-Object System.Drawing.PointF(
            ([single]($cur[0] + ($next[0] - $cur[0]) * $CornerCut)),
            ([single]($cur[1] + ($next[1] - $cur[1]) * $CornerCut)))
        $pts.Add($p1)
        $pts.Add($p2)
    }
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($pts.ToArray())
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # 六边形底 + 渐变
    if ($Size -le 24) { $pad = $Size * 0.035 } else { $pad = $Size * 0.05 }
    $hex = New-HexPath -Size $Size -Pad $pad -CornerCut 0.30
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($Size, $Size)),
        $ColFrom, $ColTo)
    $g.FillPath($brush, $hex)
    $brush.Dispose()

    # 三根递升档位柱（白色）
    $barW = [single]($Size * 0.115)
    $gap  = [single]($Size * 0.085)
    $base = [single]($Size * 0.685)
    $fracs = @(0.20, 0.31, 0.42)
    $totalW = $barW * 3 + $gap * 2
    $x0 = [single](($Size - $totalW) / 2)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255, 255))
    for ($i = 0; $i -lt 3; $i++) {
        $h = [single]($Size * $fracs[$i])
        $x = [single]($x0 + $i * ($barW + $gap))
        $y = [single]($base - $h)
        $rr = [single]($barW / 2)
        $bar = New-RoundedRectPath -X $x -Y $y -W $barW -H $h -R $rr
        $g.FillPath($white, $bar)
        $bar.Dispose()
    }
    $white.Dispose()

    $g.Dispose()
    return $bmp
}

# ── ICO 容器：小尺寸用 DIB（兼容性最好），大尺寸用 PNG（体积小）
function Get-DibPayload {
    param([System.Drawing.Bitmap]$Bmp)
    $w = $Bmp.Width; $h = $Bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))      # XOR + AND
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)
    $bw.Write([uint32]($w * $h * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)
    # XOR 位图：BGRA，自下而上
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $Bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    # AND 掩码：1bpp，行按 4 字节对齐，全 0（透明度交给 alpha 通道）
    $rowBytes = [Math]::Floor(($w + 31) / 32) * 4
    $mask = New-Object byte[] ($rowBytes * $h)
    $bw.Write($mask)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return , $bytes
}

function Get-PngPayload {
    param([System.Drawing.Bitmap]$Bmp)
    $ms = New-Object System.IO.MemoryStream
    $Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return , $bytes
}

function Save-Ico {
    param([int[]]$Sizes, [string]$Path)

    $items = @()
    foreach ($s in $Sizes) {
        $bmp = New-IconBitmap -Size $s
        if ($s -le 48) { $data = [byte[]](Get-DibPayload -Bmp $bmp); $kind = 'DIB' }
        else           { $data = [byte[]](Get-PngPayload -Bmp $bmp); $kind = 'PNG' }
        $items += [pscustomobject]@{ Size = $s; Data = $data; Kind = $kind; Bmp = $bmp }
    }

    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fs = [System.IO.File]::Create($Path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([uint16]0)                 # reserved
    $bw.Write([uint16]1)                 # type = icon
    $bw.Write([uint16]$items.Count)
    $offset = 6 + 16 * $items.Count
    foreach ($it in $items) {
        $dim = if ($it.Size -ge 256) { 0 } else { $it.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim)
        $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$it.Data.Length)
        $bw.Write([uint32]$offset)
        $offset += $it.Data.Length
    }
    foreach ($it in $items) { $bw.Write([byte[]]$it.Data) }
    $bw.Flush(); $bw.Dispose(); $fs.Dispose()
    foreach ($it in $items) { $it.Bmp.Dispose() }

    return $items
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$items = Save-Ico -Sizes $sizes -Path $OutPath

Write-Host ''
Write-Host "已生成: $OutPath" -ForegroundColor Green
Write-Host ("大小  : {0:N0} 字节" -f (Get-Item $OutPath).Length)
Write-Host '尺寸  :'
foreach ($it in $items) { Write-Host ("  {0,3}x{0,-3} {1,-4} {2,7:N0} 字节" -f $it.Size, $it.Kind, $it.Data.Length) }

# 校验：能按各尺寸加载出来
Write-Host ''
Write-Host '校验加载:'
foreach ($s in $sizes) {
    try {
        $ic = New-Object System.Drawing.Icon($OutPath, (New-Object System.Drawing.Size($s, $s)))
        Write-Host ("  {0,3}px -> {1}x{2} OK" -f $s, $ic.Width, $ic.Height) -ForegroundColor Green
        $ic.Dispose()
    }
    catch { Write-Host ("  {0,3}px -> 失败: {1}" -f $s, $_.Exception.Message) -ForegroundColor Red }
}
