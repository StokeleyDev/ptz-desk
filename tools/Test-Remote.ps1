[CmdletBinding(DefaultParameterSetName='Test')]
param(
    [Parameter(Mandatory, ParameterSetName='List')][switch]$ListAdapters,
    [Parameter(Mandatory, ParameterSetName='Test')][string]$BinPath,
    [Parameter(Mandatory, ParameterSetName='Test')][string]$AdapterId,
    [Parameter(Mandatory, ParameterSetName='Test')][ipaddress]$Address,
    [ValidateRange(1025,65535)][int]$Port = 8787
)
$ErrorActionPreference = 'Stop'
if ($ListAdapters) {
    $profiles = @(Get-NetConnectionProfile)
    foreach ($adapter in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        if ($adapter.NetworkInterfaceType -eq 'Loopback') { continue }
        $properties = $adapter.GetIPProperties()
        $ipv4 = $properties.GetIPv4Properties()
        if (!$ipv4) { continue }
        $profile = $profiles | Where-Object InterfaceIndex -eq $ipv4.Index | Select-Object -First 1
        foreach ($ip in $properties.UnicastAddresses) {
            if ($ip.Address.AddressFamily -ne 'InterNetwork') { continue }
            [pscustomobject]@{ Id = $adapter.Id; Name = $adapter.Name; IP = $ip.Address;
                Status = $adapter.OperationalStatus; Category = $profile.NetworkCategory }
        }
    }
    return
}

if (Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue) {
    throw 'Close AGL PTZ Controller before the test. The temporary copy uses the same single-instance mutex.'
}

$source = (Resolve-Path -LiteralPath $BinPath).Path
$parts = $source -split '[\\/]'
if ($parts -notcontains 'bin' -or ($parts -join '/') -match '/release/v\d+(\.\d+)+(/|$)') {
    throw 'Pass a build output directory under bin.'
}
$test = Join-Path ([IO.Path]::GetTempPath()) ('agl-remote-test-' + [guid]::NewGuid().ToString('N'))
$test = (New-Item -ItemType Directory -Path $test).FullName
$process = $null
$busy = $null
$authority = '{0}:{1}' -f $Address, $Port

function Listeners([int]$Owner) {
    & netstat.exe -ano -p tcp | Where-Object {
        $_ -match "^\s*TCP\s+(\S+:$Port)\s+\S+:0\s+\S+\s+$Owner\s*$"
    } | ForEach-Object { ($_ -split '\s+' | Where-Object { $_ })[1] }
}

function Start-Copy([bool]$Enabled) {
    @{ enabled = $Enabled; adapterId = $AdapterId; port = $Port } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath "$test/Data/remote-settings.json" -Encoding ASCII
    Start-Process -FilePath "$test/AGL.PTZ.Controller.exe" -ArgumentList '--demo' -WorkingDirectory $test -WindowStyle Hidden -PassThru
}

function Stop-Copy($App) {
    if ($App -and !$App.HasExited) {
        if (!$App.CloseMainWindow() -or !$App.WaitForExit(3000)) { $App.Kill(); $App.WaitForExit() }
    }
}

function Request([string]$Path, [string]$Method = 'GET', [string]$HostHeader = $authority) {
    $request = [Net.HttpWebRequest]::Create("http://$authority$Path")
    $request.Proxy = $null
    $request.Method = $Method
    $request.Host = $HostHeader
    $request.Timeout = 3000
    if ($Method -eq 'POST') { $request.ContentLength = 0 }
    try { $response = $request.GetResponse() }
    catch [Net.WebException] {
        if (!$_.Exception.Response) { throw }
        $response = $_.Exception.Response
    }
    try {
        $reader = [IO.StreamReader]::new($response.GetResponseStream(), [Text.Encoding]::UTF8)
        try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        [pscustomobject]@{ Status = [int]$response.StatusCode; Headers = $response.Headers;
            ContentType = $response.ContentType; Body = $body }
    }
    finally { $response.Dispose() }
}

try {
    Get-ChildItem -LiteralPath $source -Force | Where-Object Name -notin @('Data','portable.flag') |
        Copy-Item -Destination $test -Recurse -Force
    New-Item -ItemType File -Path "$test/portable.flag" | Out-Null
    New-Item -ItemType Directory -Path "$test/Data" | Out-Null
    $process = Start-Copy $false
    Start-Sleep -Milliseconds 1500
    if ($process.HasExited -or @(Listeners $process.Id).Count) { throw 'Disabled server check failed.' }
    'PASS: disabled process owns no listener on the configured port'
    Stop-Copy $process

    $process = Start-Copy $true
    for ($i=0; $i -lt 40 -and !$process.HasExited -and !(Listeners $process.Id); $i++) {
        Start-Sleep -Milliseconds 250
    }
    $sockets = @(Listeners $process.Id)
    if ($process.HasExited -or $sockets.Count -ne 1 -or $sockets[0] -ne $authority) {
        throw 'Expected one listener on the selected IP. Use an eligible Private/Domain adapter and check the port.'
    }

    $loopback = New-Object Net.Sockets.TcpClient
    try {
        $connected = $false
        try { $loopback.Connect('127.0.0.1', $Port); $connected = $true }
        catch [Net.Sockets.SocketException] { }
        if ($connected) { throw 'Unexpected listener on loopback.' }
    }
    finally { $loopback.Dispose() }
    $page = Request '/'
    if ($page.Status -ne 200 -or $page.ContentType -ne 'text/html; charset=utf-8' -or
        $page.Body -notmatch 'AGL PTZ' -or $page.Headers['Cache-Control'] -ne 'no-store' -or
        $page.Headers['Content-Security-Policy'] -notlike "*ws://$authority;*" -or
        $page.Headers['X-Content-Type-Options'] -ne 'nosniff' -or
        $page.Headers['Referrer-Policy'] -ne 'no-referrer' -or $page.Headers['Server']) {
        throw 'Unexpected phone response.'
    }
    foreach ($foreignHost in "127.0.0.1:$Port", "foreign.example:$Port") {
        $badHost = Request '/' 'GET' $foreignHost
        if ($badHost.Status -ne 400 -or $badHost.Body) { throw 'Host filter failed.' }
    }
    $head = Request '/' 'HEAD'
    if ($head.Status -ne 200 -or $head.Body -or (Request '/' 'POST').Status -ne 405 -or
        (Request '/pair').Status -ne 200 -or (Request '/app-icon.png').ContentType -ne 'image/png') {
        throw 'HTTP method or allowed route check failed.'
    }
    foreach ($path in '/index.html','/app.js','/app.css','/anything') {
        if ((Request $path).Status -ne 404) { throw "Unexpected response for $path" }
    }
    'PASS: exact IPv4 listener, loopback refused, headers, allowed and denied routes'
    Stop-Copy $process

    $busy = [Net.Sockets.TcpListener]::new($Address, $Port)
    $busy.ExclusiveAddressUse = $true
    $busy.Start()
    $process = Start-Copy $true
    Start-Sleep -Milliseconds 2500
    if ($process.HasExited -or @(Listeners $process.Id).Count) { throw 'Busy port check failed.' }
    'PASS: busy port leaves the app running without a listener'
}
finally {
    Stop-Copy $process
    if ($busy) { $busy.Stop() }
    $item = Get-Item -LiteralPath $test -Force
    if ($item.FullName -eq $test -and $item.Name -like 'agl-remote-test-*' -and
        !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}
