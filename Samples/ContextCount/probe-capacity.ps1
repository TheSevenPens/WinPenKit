<#
.SYNOPSIS
Hold a bounded context allocation attempt while capturing fresh-process controls.
.DESCRIPTION
This changes Wintab state. Read README.md and confirm recovery access first.
The holder closes every successful open after two minutes, even if a later open
fails. A failed WTOpen can itself leave residue on the tested driver. No service
is restarted and no process is forcibly killed by this script. On a timeout,
preserve the recorded state and inspect the named process before recovery.
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'investigate.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw 'Build investigate.exe first.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory.' }
$output = (New-Item -ItemType Directory -Path $OutputDirectory).FullName

function Invoke-Probe([string]$Name, [string[]]$ProbeArguments) {
    $process = Start-Process -FilePath $probe -ArgumentList $ProbeArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output "$Name.csv") -RedirectStandardError (Join-Path $output "$Name-errors.txt")
    if (-not $process.WaitForExit(30000)) {
        throw "Probe $Name timed out; process $($process.Id) was left alive to preserve state."
    }
    [pscustomobject]@{ utc=[DateTime]::UtcNow.ToString('o'); name=$Name; pid=$process.Id; exit_code=$process.ExitCode } |
        Export-Csv -LiteralPath (Join-Path $output 'process-exits.csv') -NoTypeInformation -Append
}

function Save-DriverMetrics([string]$Name) {
    $service = Get-Service WTabletServicePro
    Get-Process -Name WTabletServicePro,Wacom_Tablet,Wacom_TabletUser -ErrorAction SilentlyContinue |
        Select-Object @{n='utc';e={[DateTime]::UtcNow.ToString('o')}},ProcessName,Id,
            @{n='service_status';e={[string]$service.Status}},HandleCount,
            @{n='threads';e={$_.Threads.Count}},PrivateMemorySize64,WorkingSet64 |
        Export-Csv -LiteralPath (Join-Path $output "$Name.csv") -NoTypeInformation
}

Invoke-Probe 'before' @('snapshot')
Save-DriverMetrics 'metrics-before'
$holderFile = Join-Path $output 'holder.csv'
$holder = Start-Process -FilePath $probe -ArgumentList @('lifecycle',128,'clean','system',-1,120000) -WindowStyle Hidden -PassThru -RedirectStandardOutput $holderFile -RedirectStandardError (Join-Path $output 'holder-errors.txt')
[pscustomobject]@{ utc=[DateTime]::UtcNow.ToString('o'); holder_pid=$holder.Id; attempted_opens=128; hold_ms=120000 } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'experiment.json')
$deadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    $rows = @(Import-Csv -LiteralPath $holderFile)
    $opens = @($rows | Where-Object event -eq 'open')
    $finishedOpening = $opens.Count -eq 128 -or @($opens | Where-Object result -eq '0').Count -gt 0
    if ($holder.HasExited -or [DateTime]::UtcNow -ge $deadline) {
        throw "Holder $($holder.Id) exited early or did not finish opening within 30 seconds; preserve its output."
    }
} while (-not $finishedOpening)

Invoke-Probe 'held-info' @('info')
Save-DriverMetrics 'metrics-held'
Invoke-Probe 'held-manager' @('manager')
Invoke-Probe 'held-system' @('lifecycle',1,'clean','system')
Invoke-Probe 'held-digitizer' @('lifecycle',1,'clean','digitizer')
Invoke-Probe 'held-device0' @('lifecycle',1,'clean','system',0)
Invoke-Probe 'held-device1' @('lifecycle',1,'clean','system',1)
Invoke-Probe 'held-after-controls' @('snapshot')
if (-not $holder.WaitForExit(150000)) {
    throw "Holder $($holder.Id) did not finish cleanup; it was left alive to preserve state."
}
[pscustomobject]@{ utc=[DateTime]::UtcNow.ToString('o'); name='holder'; pid=$holder.Id; exit_code=$holder.ExitCode } |
    Export-Csv -LiteralPath (Join-Path $output 'process-exits.csv') -NoTypeInformation -Append
Invoke-Probe 'after' @('snapshot')
Invoke-Probe 'after-system' @('lifecycle',1,'clean','system')
Save-DriverMetrics 'metrics-after'
Write-Output "Capacity experiment complete: $output"
