[CmdletBinding()]
param([string]$Exe)

$ErrorActionPreference = 'Stop'
if (-not $Exe) { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\OpenMIFS.exe' }
$startupAssembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Exe).Path)
$startupType = $startupAssembly.GetType('OpenMIFS.Startup', $true)
$startupFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$script:startupAssertions = 0

function Invoke-StartupMethod([string]$Name, [object[]]$Arguments) {
    $method = $startupType.GetMethod($Name, $startupFlags)
    if (-not $method) { throw "Missing method: $Name" }
    $method.Invoke($null, $Arguments)
}

function Assert-Startup([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "STARTUP_TEST_FAIL $Name" }
    $script:startupAssertions++
}

$startupNs = 'http://schemas.microsoft.com/windows/2004/02/mit/task'
$startupFixture = @'
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Keep --tray in this description</Description></RegistrationInfo>
  <Principals><Principal id="Author"><UserId>S-1-5-21-1-2-3-1001</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>true</StopIfGoingOnBatteries><ExecutionTimeLimit>PT72H</ExecutionTimeLimit><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><Priority>5</Priority></Settings>
  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT25S</Delay></LogonTrigger></Triggers>
  <Actions Context="Author"><Exec><Command>C:\Test\Open &amp; MIFS.exe</Command><Arguments>--tray --custom</Arguments><WorkingDirectory>C:\Test</WorkingDirectory></Exec></Actions>
</Task>
'@

Assert-Startup (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupFixture)) 'old-task-detected'
$startupFixed = [string](Invoke-StartupMethod 'RepairTaskXml' @($startupFixture))
Assert-Startup (-not (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupFixed))) 'repaired-task-healthy'
Assert-Startup ($startupFixed -eq (Invoke-StartupMethod 'RepairTaskXml' @($startupFixed))) 'repair-idempotent'

$startupOldDoc = [xml]$startupFixture
$startupFixedDoc = [xml]$startupFixed
foreach ($startupPreserved in 'RegistrationInfo', 'Principals', 'Triggers', 'Actions') {
    Assert-Startup ($startupOldDoc.DocumentElement[$startupPreserved, $startupNs].OuterXml -eq $startupFixedDoc.DocumentElement[$startupPreserved, $startupNs].OuterXml) "preserve-$startupPreserved"
}
foreach ($startupPreservedSetting in 'MultipleInstancesPolicy', 'Priority') {
    Assert-Startup ($startupOldDoc.DocumentElement['Settings', $startupNs][$startupPreservedSetting, $startupNs].InnerText -eq $startupFixedDoc.DocumentElement['Settings', $startupNs][$startupPreservedSetting, $startupNs].InnerText) "preserve-$startupPreservedSetting"
}

$startupCases = @(
    @{ Name='battery-start-blocked'; Element='DisallowStartIfOnBatteries'; Value='true' },
    @{ Name='battery-stop-enabled'; Element='StopIfGoingOnBatteries'; Value='true' },
    @{ Name='72-hour-limit'; Element='ExecutionTimeLimit'; Value='PT72H' },
    @{ Name='missing-battery-start'; Element='DisallowStartIfOnBatteries'; Remove=$true },
    @{ Name='missing-battery-stop'; Element='StopIfGoingOnBatteries'; Remove=$true },
    @{ Name='missing-time-limit'; Element='ExecutionTimeLimit'; Remove=$true }
)
foreach ($startupCase in $startupCases) {
    $startupDoc = [xml]$startupFixed
    $startupNode = $startupDoc.DocumentElement['Settings', $startupNs][$startupCase.Element, $startupNs]
    if ($startupCase.Remove) { [void]$startupNode.ParentNode.RemoveChild($startupNode) }
    else { $startupNode.InnerText = $startupCase.Value }
    Assert-Startup (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupDoc.OuterXml)) $startupCase.Name
    $startupRepaired = [string](Invoke-StartupMethod 'RepairTaskXml' @($startupDoc.OuterXml))
    Assert-Startup (-not (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupRepaired))) "$($startupCase.Name)-repaired"
}

foreach ($startupLegacyArgs in '', '--custom', '--trayExtra') {
    $startupDoc = [xml]$startupFixed
    $startupDoc.DocumentElement['Actions', $startupNs]['Exec', $startupNs]['Arguments', $startupNs].InnerText = $startupLegacyArgs
    Assert-Startup (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupDoc.OuterXml)) "legacy-args-$startupLegacyArgs"
    $startupRepaired = [xml](Invoke-StartupMethod 'RepairTaskXml' @($startupDoc.OuterXml))
    Assert-Startup ($startupRepaired.DocumentElement['Actions', $startupNs]['Exec', $startupNs]['Arguments', $startupNs].InnerText -eq ($startupLegacyArgs + ' --tray').Trim()) "preserve-legacy-args-$startupLegacyArgs"
}

$startupDoc = [xml]$startupFixed
$startupSettingsNode = $startupDoc.DocumentElement['Settings', $startupNs]
[void]$startupDoc.DocumentElement.RemoveChild($startupSettingsNode)
Assert-Startup (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupDoc.OuterXml)) 'missing-settings-detected'
Assert-Startup (-not (Invoke-StartupMethod 'TaskXmlNeedsRepair' @([string](Invoke-StartupMethod 'RepairTaskXml' @($startupDoc.OuterXml))))) 'missing-settings-repaired'

$startupNewPath = 'C:\测试 & tools\OpenMIFS.exe'
$startupNewSid = 'S-1-5-21-1-2-3-1001'
$startupNew = [string](Invoke-StartupMethod 'CreateTaskXml' @($startupNewPath, $startupNewSid))
$startupNewDoc = [xml]$startupNew
Assert-Startup (-not (Invoke-StartupMethod 'TaskXmlNeedsRepair' @($startupNew))) 'new-task-healthy'
Assert-Startup ($startupNewDoc.Task.Actions.Exec.Command -eq $startupNewPath) 'path-xml-escaping'
Assert-Startup ($startupNewDoc.Task.Principals.Principal.UserId -eq $startupNewSid) 'principal-sid'
Assert-Startup ($startupNewDoc.Task.Principals.Principal.RunLevel -eq 'HighestAvailable') 'highest-run-level'
Assert-Startup ($startupNewDoc.Task.Triggers.LogonTrigger.Delay -eq 'PT10S') 'logon-delay'

foreach ($startupInvalid in '<NotATask/>', '<Task xmlns="urn:wrong"/>', ('<Task xmlns="' + $startupNs + '"><Settings/></Task>')) {
    $startupRejected = $false
    try { [void](Invoke-StartupMethod 'RepairTaskXml' @($startupInvalid)) }
    catch { $startupRejected = $true }
    Assert-Startup $startupRejected 'reject-invalid-task'
}

Write-Output "STARTUP_TEST_OK assertions=$script:startupAssertions"
