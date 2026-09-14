<#
.SYNOPSIS
Compare fresh Wintab counters before and after an interval with no probe readers.
.DESCRIPTION
Run after observe.ps1 and other investigation probes have exited. This script
reads one baseline, waits without querying Wintab, then launches a fresh reader.
It does not stop other applications and therefore cannot guarantee that unrelated
clients make no Wintab calls. It opens no contexts and changes no services.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 3600)][int]$Seconds = 600,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'investigate.exe'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory.' }
if (Get-Process -Name investigate -ErrorAction SilentlyContinue) {
    throw 'Another investigation probe is still running.'
}
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
& $probe snapshot | Set-Content -LiteralPath (Join-Path $output 'before.csv')
if ($LASTEXITCODE -ne 0) { throw 'Baseline reader failed.' }
$start = [DateTime]::UtcNow
$beforePids = @(Get-Process -Name WTabletServicePro,Wacom_Tablet,Wacom_TabletUser -ErrorAction SilentlyContinue | Select-Object ProcessName,Id)
[pscustomobject]@{ start_utc=$start.ToString('o'); seconds=$Seconds; observer_pid=$PID; driver_processes=$beforePids } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'started.json')
Start-Sleep -Seconds $Seconds
$end = [DateTime]::UtcNow
& $probe snapshot | Set-Content -LiteralPath (Join-Path $output 'after.csv')
if ($LASTEXITCODE -ne 0) { throw 'Final reader failed.' }
[pscustomobject]@{
    start_utc=$start.ToString('o'); end_utc=$end.ToString('o'); elapsed_seconds=($end-$start).TotalSeconds
    service_status=[string](Get-Service WTabletServicePro).Status
    driver_processes=@(Get-Process -Name WTabletServicePro,Wacom_Tablet,Wacom_TabletUser -ErrorAction SilentlyContinue | Select-Object ProcessName,Id)
    scope='No investigation probe readers during this interval; unrelated clients were not stopped.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'completed.json')
Write-Output "Idle comparison complete: $output"
