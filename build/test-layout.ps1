[CmdletBinding()]
param(
    [string]$OutputDir,
    [switch]$Baseline,
    [switch]$ToggleTest,
    [switch]$NoScreenshots
)

$ErrorActionPreference = 'Stop'
$layoutRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $layoutRoot 'test-output\layout-regression' }
$layoutTag = 'layout_' + [guid]::NewGuid().ToString('N')
$layoutTemp = Join-Path $env:TEMP $layoutTag
$layoutData = Join-Path $env:LOCALAPPDATA ('OpenMIFS_' + $layoutTag)
$layoutUtf8 = New-Object Text.UTF8Encoding($true)
$layoutResult = 1
$layoutOldOffline = $env:OPENMIFS_LAYOUT_OFFLINE
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
try {
    New-Item -ItemType Directory -Path $layoutTemp | Out-Null
    $layoutSources = @()
    foreach ($layoutInput in @(Get-ChildItem -LiteralPath (Join-Path $layoutRoot 'src\csharp') -Filter '*.cs' -File)) {
        $layoutText = [IO.File]::ReadAllText($layoutInput.FullName)
        $layoutText = $layoutText.Replace('Path.Combine(b, "OpenMIFS")', ('Path.Combine(b, "OpenMIFS_' + $layoutTag + '")'))
        $layoutText = $layoutText.Replace('Global\\OpenMIFS_SingleInstance', ('Global\\OpenMIFS_SingleInstance_' + $layoutTag))
        if (-not $ToggleTest) {
            $layoutText = $layoutText.Replace('Shown += delegate { LayoutAll(true); RefreshAll(); };', 'Shown += delegate { LayoutAll(true); };')
            foreach ($layoutOfflineMethod in 'RefreshAll','RefreshSensors') {
                $layoutPattern = 'public void ' + $layoutOfflineMethod + '\(\)\s*\{'
                $layoutText = [regex]::Replace($layoutText, $layoutPattern, { param($match) $match.Value + [Environment]::NewLine + '            if (Environment.GetEnvironmentVariable("OPENMIFS_LAYOUT_OFFLINE") == "1") return;' })
            }
        }
        $layoutPath = Join-Path $layoutTemp $layoutInput.Name
        [IO.File]::WriteAllText($layoutPath, $layoutText, $layoutUtf8)
        $layoutSources += $layoutPath
    }
    $layoutManifest = [IO.File]::ReadAllText((Join-Path $layoutRoot 'src\csharp\app.manifest')).Replace('requireAdministrator','asInvoker')
    $layoutManifestPath = Join-Path $layoutTemp 'app.manifest'
    [IO.File]::WriteAllText($layoutManifestPath, $layoutManifest, $layoutUtf8)
    $layoutCsc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $layoutExe = Join-Path $layoutTemp 'LayoutHarness.exe'
    $layoutMain = if ($ToggleTest) { '/main:OpenMIFS.Program' } else { '/main:LayoutHarness' }
    $layoutCompile = @('/nologo','/target:exe','/platform:anycpu','/langversion:5',$layoutMain,'/codepage:65001','/utf8output',('/out:' + $layoutExe),('/win32manifest:' + $layoutManifestPath),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Drawing.dll','/reference:System.Windows.Forms.dll','/reference:System.Management.dll','/reference:System.Xml.dll') + $layoutSources + @((Join-Path $layoutRoot 'build\LayoutHarness.cs'))
    & $layoutCsc $layoutCompile
    if ($LASTEXITCODE -ne 0) { throw 'Layout harness compilation failed' }
    [string[]]$layoutArguments = if ($ToggleTest) { @('--toggle-test=3') } else { @($OutputDir) }
    if ($Baseline) { $layoutArguments += '--baseline' }
    if ($NoScreenshots) { $layoutArguments += '--no-screenshots' }
    if (-not $ToggleTest) { $env:OPENMIFS_LAYOUT_OFFLINE = '1' }
    & $layoutExe $layoutArguments
    $layoutResult = $LASTEXITCODE
    $layoutLog = Join-Path $layoutData 'openmifs.log'
    if (Test-Path -LiteralPath $layoutLog) { Copy-Item -LiteralPath $layoutLog -Destination (Join-Path $OutputDir 'runtime.log') }
    if ($ToggleTest) {
        $layoutLogText = [IO.File]::ReadAllText($layoutLog)
        if (-not $layoutLogText.Contains('回归：全部完成，未崩溃')) { throw 'Toggle regression did not finish' }
        Get-Content -LiteralPath $layoutLog -Tail 12
        Write-Output 'TOGGLE_TEST_OK count=3'
    }
}
finally {
    $env:OPENMIFS_LAYOUT_OFFLINE = $layoutOldOffline
    foreach ($layoutCleanup in @($layoutTemp,$layoutData)) {
        $layoutFull = [IO.Path]::GetFullPath($layoutCleanup)
        $layoutParent = if ($layoutCleanup -eq $layoutTemp) { [IO.Path]::GetFullPath($env:TEMP) } else { [IO.Path]::GetFullPath($env:LOCALAPPDATA) }
        if ([IO.Path]::GetDirectoryName($layoutFull).TrimEnd('\') -ne $layoutParent.TrimEnd('\') -or -not [IO.Path]::GetFileName($layoutFull).Contains($layoutTag)) { throw 'Unexpected layout test cleanup path' }
        if (Test-Path -LiteralPath $layoutFull) { Remove-Item -LiteralPath $layoutFull -Recurse -Force }
    }
}
if ($layoutResult -ne 0) { throw ('Layout regression failed: exit ' + $layoutResult + '; see ' + $OutputDir) }
