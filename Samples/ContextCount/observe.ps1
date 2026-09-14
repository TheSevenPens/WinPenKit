<#
.SYNOPSIS
Record persistent and fresh-process Wintab counters plus driver process metrics.
.DESCRIPTION
Read-only: opens no Wintab contexts, kills no processes, changes no services.
Run in the interactive user's normal Windows session. Run deliberate lifecycle
experiments separately so their start/exit records can be compared with this CSV.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 7200)][int]$Seconds = 3600,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 10,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $PSScriptRoot 'investigate.exe'
if (-not (Test-Path -LiteralPath $probe)) { throw 'Build investigate.exe first.' }
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$watchFile = Join-Path $output 'persistent.csv'
$freshFile = Join-Path $output 'fresh.csv'
$metricsFile = Join-Path $output 'process-metrics.csv'
foreach ($path in @($watchFile, $freshFile, $metricsFile)) {
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite $path" }
}
$watch = Start-Process -FilePath $probe -ArgumentList @('watch', $Seconds, ($IntervalSeconds * 1000)) -WindowStyle Hidden -PassThru -RedirectStandardOutput $watchFile -RedirectStandardError (Join-Path $output 'persistent-errors.txt')
$timer = [Diagnostics.Stopwatch]::StartNew()
do {
    $fresh = & $probe snapshot | ConvertFrom-Csv
    if ($LASTEXITCODE -ne 0 -or -not $fresh) { throw 'Fresh counter reader failed.' }
    $fresh | Export-Csv -LiteralPath $freshFile -NoTypeInformation -Append
    $utc = [DateTime]::UtcNow.ToString('o')
    $service = Get-Service WTabletServicePro
    Get-Process -Name WTabletServicePro,Wacom_Tablet,Wacom_TabletUser -ErrorAction SilentlyContinue |
        ForEach-Object {
            [pscustomobject]@{
                utc = $utc
                name = $_.ProcessName
                pid = $_.Id
                service_status = [string]$service.Status
                handles = $_.HandleCount
                threads = $_.Threads.Count
                private_bytes = $_.PrivateMemorySize64
                working_set_bytes = $_.WorkingSet64
            }
        } | Export-Csv -LiteralPath $metricsFile -NoTypeInformation -Append
    Start-Sleep -Seconds $IntervalSeconds
} while ($timer.Elapsed.TotalSeconds -lt $Seconds)
# The read-only watcher exits on its own deadline even if this script is interrupted.
if (-not $watch.WaitForExit(15000)) { throw "Watcher $($watch.Id) has not yet exited." }
if ($watch.ExitCode -ne 0) { throw "Watcher exited with $($watch.ExitCode)." }
Write-Output "Observation complete: $output"
