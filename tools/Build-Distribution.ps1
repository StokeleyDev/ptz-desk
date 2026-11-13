$ErrorActionPreference = 'Stop'
$source = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $source 'AGL.PTZ.Controller.csproj') -Raw)).Project.PropertyGroup.Version
& (Join-Path $source 'build-release.ps1')
$release = Join-Path (Split-Path -Parent $source) "release/v$version"
$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (!$iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$iscc) { throw 'Install Inno Setup 6 first.' }
Compress-Archive -LiteralPath @("$release/Portable", "$release/Dependencies", "$release/README.txt", "$release/README-RU.md") -DestinationPath "$release/PTZ-Desk-Portable-x64.zip" -Force
& $iscc "/DReleaseDir=$release" "/DAppVersion=$version" (Join-Path $PSScriptRoot 'PTZ-Desk.iss')
if ($LASTEXITCODE) { throw 'Installer compilation failed' }
