<#
.SYNOPSIS
    Registers SDM's native messaging host with the Chromium browsers on this machine.

.DESCRIPTION
    A convenience for working from source. The installer does not use this — it calls the
    same executable directly — and neither does anything else: the registration itself
    lives in SDM.NativeHost.exe, in C#.

    That is not where it started. It used to be written here, and being written here was
    the problem twice over. Script execution is disabled by policy on a great many Windows
    machines, including at least one this was tested on, so an installer that shelled out
    to a .ps1 failed on exactly the people least able to work out why. And the host
    manifest holds absolute paths: a user whose name is not in the script host's code page
    has a profile folder that Set-Content turns into nonsense, and Windows PowerShell adds
    a byte order mark that Chrome's JSON parser will not read past.

    Doing it in .NET makes all three of those stop being questions.

.PARAMETER HostPath
    SDM.NativeHost.exe. The default is the Release build.

.PARAMETER Uninstall
    Remove the registration instead of adding it.

.EXAMPLE
    .\install-native-host.ps1
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe'),
    [switch] $Uninstall
)

$ErrorActionPreference = 'Stop'

# Tested before resolving, not after. Resolve-Path throws on a path that is not there, and
# with $ErrorActionPreference set to Stop that threw first — so this message, which says
# what to do about it, could never be reached.
if (-not (Test-Path $HostPath)) {
    throw "SDM.NativeHost.exe was not found at $HostPath. Build the solution first (dotnet build -c Release), or pass -HostPath."
}

$resolved = (Resolve-Path $HostPath).Path

if ($Uninstall) {
    & $resolved --unregister
} else {
    & $resolved --register
}

if ($LASTEXITCODE -ne 0) {
    throw "Registration failed with exit code $LASTEXITCODE."
}

"`nCheck it with:  & '$resolved' --selftest"
