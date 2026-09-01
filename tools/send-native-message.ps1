<#
.SYNOPSIS
    Sends one native messaging request to SDM's host, the way Chrome does.

.DESCRIPTION
    Frames a JSON message with a 4-byte little-endian length, feeds it to the host's
    standard input, and decodes the framed reply.

    Both streams are redirected from and to files of raw bytes rather than written through
    PowerShell's own pipes. Every text layer in between is a chance to insert a byte order
    mark, and three stray bytes are read as part of the length — which is exactly how this
    script failed the first time it was written, and how a real extension fails too.

.EXAMPLE
    .\send-native-message.ps1 -Type ping

.EXAMPLE
    .\send-native-message.ps1 -Type download -Url https://example.com/file.zip
#>
[CmdletBinding()]
param(
    [string] $HostPath = (Join-Path $PSScriptRoot '..\src\SDM.Desktop\bin\Release\net10.0\SDM.NativeHost.exe'),
    [ValidateSet('ping', 'download')]
    [string] $Type = 'ping',
    [string] $Url,
    [string] $FileName,
    [int] $TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

$resolved = (Resolve-Path $HostPath).Path

$message = @{ type = $Type }
if ($Url) { $message.url = $Url }
if ($FileName) { $message.fileName = $FileName }

$json = $message | ConvertTo-Json -Compress
$payload = [Text.Encoding]::UTF8.GetBytes($json)

$framed = [byte[]]::new(4 + $payload.Length)
[BitConverter]::GetBytes([uint32] $payload.Length).CopyTo($framed, 0)
$payload.CopyTo($framed, 4)

$requestFile = [IO.Path]::GetTempFileName()
$replyFile = [IO.Path]::GetTempFileName()
$errorFile = [IO.Path]::GetTempFileName()

try {
    [IO.File]::WriteAllBytes($requestFile, $framed)
    "sent  $json"

    $process = Start-Process -FilePath $resolved -PassThru -NoNewWindow `
        -RedirectStandardInput $requestFile `
        -RedirectStandardOutput $replyFile `
        -RedirectStandardError $errorFile

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        throw "The host did not answer within $TimeoutSeconds seconds."
    }

    $reply = [IO.File]::ReadAllBytes($replyFile)

    if ($reply.Length -lt 4) {
        throw "The host answered with $($reply.Length) bytes.`n$(Get-Content $errorFile -Raw)"
    }

    $length = [BitConverter]::ToUInt32($reply, 0)
    "reply $([Text.Encoding]::UTF8.GetString($reply, 4, $length))"
}
finally {
    Remove-Item $requestFile, $replyFile, $errorFile -Force -ErrorAction SilentlyContinue
}
