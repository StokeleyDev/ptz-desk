$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$controller = 'AGL.PTZ.Controller.csproj'
$version = ([xml](Get-Content -LiteralPath $controller -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Unexpected release version' }
$packageRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $packageRoot 'release'
$release = Join-Path $releaseRoot "v$version"
$portable = Join-Path $release 'Portable'
$dependencies = Join-Path $release 'Dependencies'
$applicationPath = Join-Path $portable 'AGL.PTZ.Controller.exe'
$legacyPath = Join-Path $packageRoot 'portable/AGL.PTZ.Controller.exe'
$dotnet = if ($env:AGL_PTZ_DOTNET) { $env:AGL_PTZ_DOTNET } else { 'dotnet' }

foreach ($app in Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue) {
    if ($app.Path -notin @($applicationPath, $legacyPath)) { continue }
    if (!$app.CloseMainWindow() -or !$app.WaitForExit(10000)) {
        throw "Close the running controller before publishing: $($app.Path)"
    }
}

if (Test-Path -LiteralPath $release) {
    $existing = Get-Item -LiteralPath $release -Force
    $parent = Get-Item -LiteralPath $releaseRoot -Force
    if ($existing.FullName -ne $release -or !$existing.PSIsContainer -or
        ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Unexpected release directory: $release"
    }
    Remove-Item -LiteralPath $existing.FullName -Recurse -Force
}

Push-Location ui
try {
    & npm.cmd ci
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    & node build.mjs
    if ($LASTEXITCODE) { throw 'UI build failed' }
} finally { Pop-Location }

$flags = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false')
& $dotnet publish $controller @flags -o $portable
if ($LASTEXITCODE) { throw 'Controller publish failed' }
$redist = Join-Path $dependencies 'redist'
New-Item -ItemType Directory -Path $redist -Force | Out-Null
New-Item -ItemType File -Path (Join-Path $portable 'portable.flag') -Force | Out-Null
Copy-Item -LiteralPath 'tools/Release-README.txt' -Destination (Join-Path $release 'README.txt')
Copy-Item -LiteralPath 'tools/dependencies/AGL.PTZ.Dependencies.cmd','tools/dependencies/Install-Dependencies.ps1' -Destination $dependencies
Copy-Item -LiteralPath 'tools/dependencies/redist-readme.txt' -Destination (Join-Path $redist 'README.txt')
$entries = @(Get-ChildItem -LiteralPath $portable -Force)
if ($entries.Count -ne 2 -or ($entries | Where-Object {
    $_.PSIsContainer -or $_.Name -notin @('AGL.PTZ.Controller.exe', 'portable.flag')
})) { throw "Unexpected contents in $portable" }
Copy-Item -LiteralPath README-RU.md -Destination $release
Write-Output $release

