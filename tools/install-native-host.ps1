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

# Windows PowerShell 5.1 writes a byte-order mark for -Encoding utf8, and a BOM in front
# of a native messaging manifest is three bytes Chrome's JSON parser has no idea what to
# do with. The host then does not register, and the browser says only "host not found".
function Write-Utf8NoBom {
    param([Parameter(ValueFromPipeline = $true)] [string] $Content, [string] $Path)

    process {
        [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding $false))
    }
}

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

# Tested before resolving, not after. Resolve-Path throws on a path that is not there,
# and with $ErrorActionPreference set to Stop that threw first — so the message below,
# which says what to do about it, could never be reached.
if (-not (Test-Path $HostPath)) {
    throw "SDM.NativeHost.exe was not found at $HostPath. Build the solution first (dotnet build -c Release), or pass -HostPath."
}

$resolved = (Resolve-Path $HostPath).Path

# A Chrome extension id is thirty-two letters from a to p. Anything else registers happily
# and then refuses every connection, with the browser reporting only "host not found".
if ($ExtensionId -notmatch '^[a-p]{32}$') {
    Write-Warning "'$ExtensionId' is not a valid extension id. The host will register, and no extension will be able to talk to it."
}

# Written beside the user's own data, not beside the executable.
#
# It used to go into the build output folder, which is the one directory guaranteed not to
# survive: `dotnet clean`, `git clean -xdf`, or deleting bin\ to force a rebuild all take
# the manifest with them. The registry key stays behind pointing at a file that is no
# longer there, so Chrome reports "Specified native messaging host not found" and the only
# clue is a registry value that looks perfectly correct.
#
# The path inside the manifest still names the executable in the build output, and that
# one is restored by the next build. Only the manifest itself had nowhere safe to live.
$manifestDirectory = Join-Path $env:LOCALAPPDATA 'SDM'
$null = New-Item -ItemType Directory -Force -Path $manifestDirectory
$manifestPath = Join-Path $manifestDirectory "$hostName.json"

@{
    name            = $hostName
    description     = 'Speed Download Manager bridge'
    path            = $resolved
    type            = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json -Depth 4 | Write-Utf8NoBom -Path $manifestPath

"manifest $manifestPath"

foreach ($browser in $browsers.GetEnumerator()) {
    $key = Join-Path $browser.Value $hostName
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name '(default)' -Value $manifestPath
    "registered  $($browser.Key)"
}

"`nCheck it with:  & '$resolved' --selftest"
