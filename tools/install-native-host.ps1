<#
.SYNOPSIS
    Registers SDM's native messaging host with Chrome, Edge and Brave.

.DESCRIPTION
    Writes the host manifest beside the executable and points each browser at it through
    the registry. Everything is written under HKCU: registering per user needs no
    administrator, and a download manager has no business writing machine-wide keys.

.PARAMETER ExtensionId
    The extension allowed to talk to the host. Chrome refuses any other caller, so this
    is the single most important value here — an installation with the placeholder id
    will register cleanly and then never connect.

.EXAMPLE
    .\install-native-host.ps1 -ExtensionId abcdefghijklmnopabcdefghijklmnop
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe'),
    [string] $ExtensionId = 'REPLACE_WITH_EXTENSION_ID',
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

$hostName = 'com.sdm.host'
$browsers = @{
    'Chrome' = 'HKCU:\Software\Google\Chrome\NativeMessagingHosts'
    'Edge'   = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts'
    'Brave'  = 'HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts'
}

if ($Uninstall) {
    foreach ($browser in $browsers.GetEnumerator()) {
        $key = Join-Path $browser.Value $hostName
        if (Test-Path $key) {
            Remove-Item $key -Force
            "removed  $($browser.Key)"
        }
    }
    return
}

$resolved = (Resolve-Path $HostPath).Path
if (-not (Test-Path $resolved)) { throw "SDM.NativeHost.exe was not found at $resolved. Build the solution first." }

if ($ExtensionId -eq 'REPLACE_WITH_EXTENSION_ID') {
    Write-Warning 'No extension id given. The host will register but no extension will be allowed to talk to it.'
}

$manifestPath = Join-Path (Split-Path $resolved) "$hostName.json"

@{
    name            = $hostName
    description     = 'Speed Download Manager bridge'
    path            = $resolved
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8

"manifest $manifestPath"

foreach ($browser in $browsers.GetEnumerator()) {
    $key = Join-Path $browser.Value $hostName
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value $manifestPath
    "registered  $($browser.Key)"
}

"`nCheck it with:  & '$resolved' --selftest"
