<#
.SYNOPSIS
    Says whether this account can restart the tablet driver's service without a UAC prompt.

.DESCRIPTION
    Restarting the tablet service is the cure for a driver that has stopped handing out Wintab
    contexts, and it is the only way to clear leaked ones. By default it needs administrator
    rights, so every restart raises a prompt -- which is fine once and a nuisance in a test run
    that wants to reset the driver between cases.

    A service's permissions can be widened so that one account may start and stop that one service
    and nothing else. This script says whether that has been done, and prints the exact commands
    to do it or to undo it.

    It asks Windows rather than reading the descriptor and guessing: the service is opened for
    SERVICE_START | SERVICE_STOP and the answer is whether that succeeded. Opening a service does
    not start, stop or change it, so running this is harmless.

.PARAMETER ServiceName
    The service to ask about. Found automatically when omitted.

.EXAMPLE
    .\Test-TabletServiceAccess.ps1

.EXAMPLE
    .\Test-TabletServiceAccess.ps1 -ServiceName Spooler
    Any service will do, which is also how to see what the refused answer looks like.
#>
[CmdletBinding()]
param([string]$ServiceName)

Add-Type -Namespace Svc -Name Api -MemberDefinition @'
[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr OpenSCManagerW(string machine, string database, uint access);

[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

[DllImport("advapi32.dll", SetLastError = true)]
public static extern bool CloseServiceHandle(IntPtr handle);
'@

$SC_MANAGER_CONNECT  = 0x0001
$SERVICE_QUERY_STATUS = 0x0004
$SERVICE_START        = 0x0010
$SERVICE_STOP         = 0x0020

# The vendors whose service names are known. Nothing here is Wacom-specific except that Wacom is
# the only one that has been tested; the permission change works the same way for any of them.
$Vendors = 'wacom|wtablet|huion|xp-?pen|gaomon|xencelabs|veikk|ugee|parblo|tablet|pentablet'

function Find-TabletService {
    Get-Service |
        Where-Object { $_.Name -match $Vendors -and $_.Name -ne 'TabletInputService' } |
        Select-Object -First 1
}

if (-not $ServiceName) {
    $found = Find-TabletService
    if (-not $found) {
        Write-Host 'No tablet service found on this machine.' -ForegroundColor Yellow
        Write-Host 'Nothing to configure: with no Wintab driver there are no contexts to leak.'
        return
    }
    $ServiceName = $found.Name
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ''
Write-Host "service   $ServiceName"
Write-Host "account   $($identity.Name)"
Write-Host "elevated  $elevated"

# Ask the service control manager, rather than reading the descriptor and interpreting it.
# NullString rather than $null: PowerShell marshals $null to a string parameter as an empty
# string, and an empty machine name is not the local machine, it is an invalid name.
$scm = [Svc.Api]::OpenSCManagerW([NullString]::Value, [NullString]::Value, $SC_MANAGER_CONNECT)
if ($scm -eq [IntPtr]::Zero) {
    Write-Host ''
    Write-Host 'Could not connect to the service control manager.' -ForegroundColor Red
    return
}

$wanted = $SERVICE_QUERY_STATUS -bor $SERVICE_START -bor $SERVICE_STOP
$handle = [Svc.Api]::OpenServiceW($scm, $ServiceName, $wanted)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
$allowed = $handle -ne [IntPtr]::Zero

if ($allowed) { [void][Svc.Api]::CloseServiceHandle($handle) }
[void][Svc.Api]::CloseServiceHandle($scm)

$sddl = (sc.exe sdshow $ServiceName | Where-Object { $_.Trim() }) -join ''
$sid = $identity.User.Value

Write-Host ''
if ($allowed) {
    Write-Host 'This account can start and stop the service with no prompt.' -ForegroundColor Green
    if (-not $elevated) {
        Write-Host 'Restart-Service will work from an ordinary window:'
        Write-Host "    Restart-Service $ServiceName -Force"
    }
    else {
        Write-Host 'Note that this window is already elevated, which is reason enough on its own.'
        Write-Host 'Run this again from an ordinary window to see whether the permission is set.'
    }
}
else {
    $why = if ($err -eq 5) { 'access denied' } else { "error $err" }
    Write-Host "This account cannot start or stop the service ($why)." -ForegroundColor Yellow
    Write-Host 'Every restart will raise a UAC prompt until that changes.'
    Write-Host ''
    if ($ServiceName -notmatch $Vendors) {
        Write-Host ''
        Write-Host "$ServiceName does not look like a tablet service. The line below widens the" -ForegroundColor Yellow
        Write-Host 'permissions on whatever is named, so read it before running it.' -ForegroundColor Yellow
    }

    Write-Host ''
    Write-Host 'To grant it, run this ONCE in an elevated PowerShell. It is the descriptor the'
    Write-Host 'service has now with one entry added, granting this account query, start and stop'
    Write-Host 'on this one service and nothing else:'
    Write-Host ''
    Write-Host "    sc.exe sdset $ServiceName `"$sddl(A;;LCRPWP;;;$sid)`"" -ForegroundColor Cyan
    Write-Host ''
    Write-Host 'Keep this line. It puts the service back exactly as it is now:'
    Write-Host ''
    Write-Host "    sc.exe sdset $ServiceName `"$sddl`"" -ForegroundColor Cyan
}

Write-Host ''
Write-Host 'descriptor now'
Write-Host "    $sddl"
Write-Host ''
Write-Host 'A driver update usually recreates the service and discards this, so if prompts come'
Write-Host 'back one day, that is why. Run this script again and reapply the line it prints.'
Write-Host ''
