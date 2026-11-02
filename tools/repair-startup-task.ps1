#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$TaskName = 'OpenMIFS',
    [string]$OutputDir = (Join-Path $env:LOCALAPPDATA ('OpenMIFS\startup-repair-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$startupResultPath = Join-Path $OutputDir 'result.json'
try {
    $startupTask = Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
    $startupBackupPath = Join-Path $OutputDir ($TaskName + '-before.xml')
    if (Test-Path -LiteralPath $startupBackupPath) { throw "Backup already exists: $startupBackupPath" }
    [IO.File]::WriteAllText($startupBackupPath, (Export-ScheduledTask -TaskName $TaskName -TaskPath '\'), [Text.Encoding]::Unicode)
    $startupBefore = [ordered]@{
        DisallowStartIfOnBatteries = $startupTask.Settings.DisallowStartIfOnBatteries
        StopIfGoingOnBatteries = $startupTask.Settings.StopIfGoingOnBatteries
        ExecutionTimeLimit = $startupTask.Settings.ExecutionTimeLimit
    }

    $startupTask.Settings.DisallowStartIfOnBatteries = $false
    $startupTask.Settings.StopIfGoingOnBatteries = $false
    $startupTask.Settings.ExecutionTimeLimit = 'PT0S'
    Set-ScheduledTask -TaskName $TaskName -TaskPath '\' -Settings $startupTask.Settings -ErrorAction Stop | Out-Null

    $startupActual = Get-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
    if ($startupActual.Settings.DisallowStartIfOnBatteries -or $startupActual.Settings.StopIfGoingOnBatteries -or [Xml.XmlConvert]::ToTimeSpan($startupActual.Settings.ExecutionTimeLimit) -ne [TimeSpan]::Zero) {
        throw 'Task settings verification failed'
    }
    [IO.File]::WriteAllText((Join-Path $OutputDir ($TaskName + '-after.xml')), (Export-ScheduledTask -TaskName $TaskName -TaskPath '\'), [Text.Encoding]::Unicode)

    $startupProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'OpenMIFS.exe'")
    if (-not $startupProcesses.Count) {
        Start-ScheduledTask -TaskName $TaskName -TaskPath '\' -ErrorAction Stop
        for ($startupAttempt = 0; $startupAttempt -lt 10 -and -not $startupProcesses.Count; $startupAttempt++) {
            Start-Sleep -Milliseconds 500
            $startupProcesses = @(Get-CimInstance Win32_Process -Filter "Name = 'OpenMIFS.exe'")
        }
    }
    $startupResult = [ordered]@{
        Success = $true
        TaskName = $TaskName
        Backup = $startupBackupPath
        Before = $startupBefore
        After = [ordered]@{
            DisallowStartIfOnBatteries = $startupActual.Settings.DisallowStartIfOnBatteries
            StopIfGoingOnBatteries = $startupActual.Settings.StopIfGoingOnBatteries
            ExecutionTimeLimit = $startupActual.Settings.ExecutionTimeLimit
        }
        ProcessIds = @($startupProcesses | ForEach-Object { $_.ProcessId })
    }
    $startupResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $startupResultPath -Encoding UTF8
    if (-not $startupProcesses.Count) { throw 'Task repaired, but OpenMIFS process was not observed' }
    Write-Output ('STARTUP_REPAIR_OK result=' + $startupResultPath)
}
catch {
    [ordered]@{ Success=$false; Error=$_.Exception.Message; Backup=$startupBackupPath } | ConvertTo-Json | Set-Content -LiteralPath $startupResultPath -Encoding UTF8
    throw
}
