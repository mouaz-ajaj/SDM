<#
.SYNOPSIS
    Registers SDM's native messaging host with Chrome, Edge and Brave.

.DESCRIPTION
    Writes the host manifest beside the executable and points each browser at it through
    the registry. Everything is written under HKCU: registering per user needs no
    administrator, and a download manager has no business writing machine-wide keys.

.PARAMETER ExtensionId
    The extension allowed to talk to the host. Chrome refuses every other caller, so this
    is the single most important value here — a wrong id registers cleanly and then never
    connects.

    The default is SDM's own id, and it is fixed rather than guessed: extension/manifest.json
    carries the matching public key, so Chrome derives this same id however often the
    extension is reloaded and wherever the folder is moved. Pass this parameter only when
    running a differently-keyed build.

.EXAMPLE
    .\install-native-host.ps1
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe'),
    [string] $ExtensionId = 'efcijjodjgojhelobljfkbigkndfeobe',
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

# A Chrome extension id is thirty-two letters from a to p. Anything else registers happily
# and then refuses every connection, with the browser reporting only "host not found".
if ($ExtensionId -notmatch '^[a-p]{32}$') {
    Write-Warning "'$ExtensionId' is not a valid extension id. The host will register, and no extension will be able to talk to it."
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
