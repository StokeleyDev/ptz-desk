param([Parameter(Mandatory)][string]$BinPath,[Parameter(Mandatory)][string]$AdapterId,
    [ValidateRange(1025,65535)][int]$Port=8787)
& (Join-Path $PSScriptRoot 'Test-RemoteSocket.ps1') @PSBoundParameters -AudioOnly
