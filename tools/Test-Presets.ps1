param([Parameter(Mandatory)][string]$BinPath,[Parameter(Mandatory)][string]$AdapterId,
    [ValidateRange(1025,65535)][int]$Port=8787)
# USB presets with the fake test camera: save a position, move away, recall it, clear it.
$ErrorActionPreference='Stop'
if(Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue){throw 'Close AGL PTZ Controller before running the test.'}
$bin=(Resolve-Path -LiteralPath $BinPath).Path
if(($bin -split '[\\/]') -notcontains 'bin'){throw 'Use bin output.'}
$test=(New-Item -ItemType Directory -Path (Join-Path $env:TEMP ('agl-presets-'+[guid]::NewGuid()))).FullName
$app=$null
$sockets=@()
Add-Type -AssemblyName System.Net.Http
function Send($ws,$message){
    $bytes=[Text.Encoding]::UTF8.GetBytes(($message|ConvertTo-Json -Compress -Depth 5))
    $null=$ws.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
$script:last=$null
function Read-Until($ws,[scriptblock]$match,[int]$TimeoutMs=5000){
    $timeout=[Threading.CancellationTokenSource]::new($TimeoutMs)
    try {
        $buffer=[byte[]]::new(8192)
        do {
            $stream=[IO.MemoryStream]::new()
            try {
                do {
                    $part=$ws.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
                    if($part.MessageType -eq 'Close'){throw 'Unexpected socket closure.'}
                    $stream.Write($buffer,0,$part.Count)
                } until($part.EndOfMessage)
                $data=ConvertFrom-Json ([Text.Encoding]::UTF8.GetString($stream.ToArray()))
                if($data.type -eq 'state'){$script:last=$data}
                if(& $match $data){return $data}
            } finally {$stream.Dispose()}
        } while($true)
    } finally {$timeout.Dispose()}
}
function Pan-Log {
    $lines=@(Get-Content -LiteralPath "$test/Data/test-motion.log" | Where-Object { $_ -match ' Pan ' })
    if(!$lines){return $null}
    return [int]($lines[-1].Split(' ')[2])
}
function Drive-Stick($ws,$state,[int]$Count=20){
    for($i=0;$i -lt $Count;$i++){
        $script:sequence++
        Send $ws @{type='input';seq=$script:sequence;cameraId=$state.activeId;generation=$state.generation;stick=@{x=0.75;y=0};zoomHold=0}
        Start-Sleep -Milliseconds 50
    }
    Start-Sleep -Milliseconds 700
}
try {
    Get-ChildItem -LiteralPath $bin -Force | Where-Object Name -notin @('Data','portable.flag') | Copy-Item -Destination $test -Recurse
    New-Item -ItemType File -Path "$test/portable.flag"|Out-Null
    New-Item -ItemType Directory -Path "$test/Data"|Out-Null
    @{enabled=$true;adapterId=$AdapterId;port=$Port}|ConvertTo-Json -Compress | Set-Content -LiteralPath "$test/Data/remote-settings.json" -Encoding ASCII
    $saved=$env:AGL_PTZ_TEST
    try { $env:AGL_PTZ_TEST='1'; $app=Start-Process -FilePath "$test/AGL.PTZ.Controller.exe" -WindowStyle Hidden -PassThru }
    finally { $env:AGL_PTZ_TEST=$saved }
    $path="$test/Data/test-pairing.txt"
    for($i=0;$i -lt 120 -and !(Test-Path $path);$i++){Start-Sleep -Milliseconds 125}
    $uri=[uri][IO.File]::ReadAllText($path)
    $origin=$uri.GetLeftPart([UriPartial]::Authority)
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.UseProxy=$false
    $client=[Net.Http.HttpClient]::new($handler)
    $client.DefaultRequestHeaders.Add('Origin',$origin)
    $body=[Net.Http.StringContent]::new((@{code=$uri.Fragment.Substring(6);name='Preset test'}|ConvertTo-Json -Compress),[Text.Encoding]::UTF8,'application/json')
    if(!$client.PostAsync("$origin/api/pair",$body).GetAwaiter().GetResult().IsSuccessStatusCode){throw 'Pairing failed.'}
    $ws=[Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.Proxy=[Net.WebProxy]::new()
    $ws.Options.SetRequestHeader('Origin',$origin)
    $ws.Options.Cookies=$handler.CookieContainer
    $sockets+=,$ws
    $null=$ws.ConnectAsync([uri]("ws://"+$uri.Authority+"/ws"),[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Send $ws @{type='hello';protocol=1}
    $null=Read-Until $ws {param($m) $m.type -eq 'state'}
    Send $ws @{type='bankActivate';cameraId='test-usb';requestId='t'}
    $state=Read-Until $ws {param($m) $m.type -eq 'state' -and $m.canMove -and $m.canPreset}
    if(@($state.presets).Count -ne 8 -or @($state.presets | Where-Object {$_}).Count){throw 'Presets should start empty.'}
    'PASS: camera ready, canPreset, 8 empty slots'
    $sequence=0
    Drive-Stick $ws $state
    $p1=Pan-Log
    Send $ws @{type='presetSave';value=3}
    $null=Read-Until $ws {param($m) $m.type -eq 'state' -and $m.presets[2]}
    'PASS: preset 3 saved at Pan '+$p1
    Drive-Stick $ws $state
    $p2=Pan-Log
    if($p2 -eq $p1){throw 'Camera did not move away from the preset.'}
    Send $ws @{type='presetGo';value=3}
    Start-Sleep -Milliseconds 600
    if((Pan-Log) -ne $p1){throw "Recall gave Pan $(Pan-Log), expected $p1."}
    'PASS: preset 3 recalled Pan '+$p1+' (was '+$p2+')'
    $json=Get-Content -LiteralPath "$test/Data/cameras.json" -Raw
    if($json -notmatch '"Presets":\[\{"Slot":3'){throw 'Preset is not stored in cameras.json.'}
    'PASS: stored in the camera profile'
    $count=@(Get-Content "$test/Data/test-motion.log").Count
    Send $ws @{type='presetGo';value=9}
    Send $ws @{type='presetGo';value=5}
    Send $ws @{type='presetSave';value='x'}
    Start-Sleep -Milliseconds 500
    if(@(Get-Content "$test/Data/test-motion.log").Count -ne $count){throw 'Invalid or empty preset moved the camera.'}
    Send $ws @{type='presetClear';value=3}
    $null=Read-Until $ws {param($m) $m.type -eq 'state' -and !$m.presets[2]}
    Send $ws @{type='presetGo';value=3}
    Start-Sleep -Milliseconds 400
    if(@(Get-Content "$test/Data/test-motion.log").Count -ne $count){throw 'Cleared preset still moved the camera.'}
    'PASS: invalid, empty and cleared presets do nothing'
} finally {
    foreach($socket in $sockets){$socket.Dispose()}
    if($app -and !$app.HasExited){$app.Kill();$app.WaitForExit()}
    $item=Get-Item -LiteralPath $test -ErrorAction SilentlyContinue
    if($item -and $item.Name -like 'agl-presets-*' -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.Contains($test) } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        for($attempt=0;$attempt -lt 40;$attempt++){
            try { Remove-Item -LiteralPath $test -Recurse -Force -ErrorAction Stop; break }
            catch { if($attempt -eq 39){Write-Warning "Could not remove $test"}; Start-Sleep -Milliseconds 500 }
        }
    }
}
