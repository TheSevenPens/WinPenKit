<#
.SYNOPSIS
Extract selected tablet metadata from existing Wacom preferences without loading Wintab.
.DESCRIPTION
Reads no sensor IDs, serial numbers, application settings, or pen-button mappings into
the output. Preference entries are not an authoritative live Wintab device mapping.
No preferences, driver state, services, or security settings are changed.
#>
[CmdletBinding()]
param(
    [string]$PreferencePath = (Join-Path $env:APPDATA 'WTablet\Wacom_Tablet.dat'),
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputPath) { throw 'Use a new output path.' }
$document = New-Object System.Xml.XmlDocument
$document.XmlResolver = $null
$document.Load($PreferencePath)
$rows = @($document.SelectNodes('/root/TabletArray/ArrayElement') | ForEach-Object {
    $element = $_
    $values = [ordered]@{}
    foreach ($field in @('TabletName','DefaultTabName','IsVirtual','TabletPhysicallyOn',
                         'TabletOn1Off0','TabletType','TabletXDimension','TabletYDimension')) {
        $node = $element.SelectSingleNode($field)
        $values[$field] = if ($node) { $node.InnerText } else { $null }
    }
    [pscustomobject]$values
})
[pscustomobject]@{
    utc = [DateTime]::UtcNow.ToString('o')
    source = 'Existing WTablet/Wacom_Tablet.dat; selected tablet metadata only'
    scope = 'Read-only preferences, not authoritative live Wintab device mapping. No sensor IDs, serials, or application settings recorded.'
    profiles = $rows
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath
