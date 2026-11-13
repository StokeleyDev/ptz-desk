param([Parameter(Mandatory)][string]$BinPath,[Parameter(Mandatory)][string]$AdapterId,
    [ValidateRange(1025,65535)][int]$Port=8787,[switch]$AudioOnly)
$ErrorActionPreference='Stop'
if(Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue){throw 'Close AGL PTZ Controller before running the test.'}
$bin=(Resolve-Path -LiteralPath $BinPath).Path
if(($bin -split '[\\/]') -notcontains 'bin'){throw 'Use bin output.'}
$test=(New-Item -ItemType Directory -Path (Join-Path $env:TEMP ('agl-socket-'+[guid]::NewGuid()))).FullName
$app=$null
$sockets=@()
function Send($ws,$message){
    $bytes=[Text.Encoding]::UTF8.GetBytes(($message|ConvertTo-Json -Compress))
    $null=$ws.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
function Read-Until($ws,[scriptblock]$match){
    $timeout=[Threading.CancellationTokenSource]::new(5000)
    try {
        $buffer=[byte[]]::new(4096)
        do {
            $stream=[IO.MemoryStream]::new()
            try {
                do {
                    $part=$ws.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult()
                    if($part.MessageType -eq 'Close'){throw 'Unexpected socket closure.'}
                    $stream.Write($buffer,0,$part.Count)
                } until($part.EndOfMessage)
                $json=[Text.Encoding]::UTF8.GetString($stream.ToArray())
                $data=ConvertFrom-Json $json
                if($data.type -eq 'state' -and $json -match '"(username|target|hasPassword|report)"|agl-test-usb|(?:\d{1,3}\.){3}\d{1,3}'){throw 'Private state leaked.'}
                if(& $match $data){return $data}
            } finally {$stream.Dispose()}
        } while($true)
    } finally {$timeout.Dispose()}
}
function Connect-TestPeer {
    $path=Join-Path $test 'Data/test-pairing.txt'
    for($i=0;$i -lt 80;$i++){
        if(Test-Path -LiteralPath $path){
            $url=[IO.File]::ReadAllText($path)
            if($url -and $url -ne $script:lastOffer){break}
        }
        Start-Sleep -Milliseconds 125
    }
    if($i -eq 80){throw 'No new test pairing offer.'}
    $script:lastOffer=$url
    $uri=[uri]$url
    $origin=$uri.GetLeftPart([UriPartial]::Authority)
    $code=$uri.Fragment.Substring(6)
    $handler=[Net.Http.HttpClientHandler]::new()
    $handler.UseProxy=$false
    $client=[Net.Http.HttpClient]::new($handler)
    try {
        $client.DefaultRequestHeaders.Add('Origin',$origin)
        $body=[Net.Http.StringContent]::new((@{code=$code;name='Socket test'}|ConvertTo-Json -Compress),[Text.Encoding]::UTF8,'application/json')
        $reply=$client.PostAsync("$origin/api/pair",$body).GetAwaiter().GetResult()
        if(!$reply.IsSuccessStatusCode){throw "Pairing failed: $($reply.StatusCode)"}
        $ws=[Net.WebSockets.ClientWebSocket]::new()
        $ws.Options.Proxy=[Net.WebProxy]::new()
        $ws.Options.SetRequestHeader('Origin',$origin)
        $ws.Options.Cookies=$handler.CookieContainer
        $script:sockets+=,$ws
        $null=$ws.ConnectAsync([uri]("ws://"+$uri.Authority+"/ws"),[Threading.CancellationToken]::None).GetAwaiter().GetResult()
        Send $ws @{type='hello';protocol=1}
        $null=Read-Until $ws {param($m) $m.type -eq 'state'}
        return $ws
    } finally {$client.Dispose()}
}
function Move-Test($ws,$state,[int]$Count=20,[switch]$Flood){
    for($i=0;$i -lt $Count;$i++){
        $script:sequence++
        Send $ws @{type='input';seq=$script:sequence;cameraId=$state.activeId;generation=$state.generation;
            stick=@{x=0.75;y=0};zoomHold=0}
        if(!$Flood){Start-Sleep -Milliseconds 50}
    }
}
function Tick {
    return [PtzTestClock]::GetTickCount64()
}
function Check-Stop([long]$Since,[int]$Limit){
    Start-Sleep -Milliseconds ($Limit+150)
    $last=Get-Content -LiteralPath "$test/Data/test-motion.log" -Tail 1
    $delta=[long]($last.Split(' ')[0])-$Since
    if($delta -gt $Limit){throw "Motion continued for $delta ms, expected <= $Limit ms."}
    Start-Sleep -Milliseconds 200
    if((Get-Content -LiteralPath "$test/Data/test-motion.log" -Tail 1) -ne $last){throw 'Motion resumed.'}
    return [Math]::Max(0,$delta)
}
try {
    Add-Type -AssemblyName System.Net.Http
    if(!('PtzTestClock' -as [type])){
        Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class PtzTestClock {
    [DllImport("kernel32.dll")] public static extern long GetTickCount64();
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(int id, WindowCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    public static bool Close(Process process) {
        bool posted = false;
        foreach (ProcessThread thread in process.Threads) {
            EnumThreadWindows(thread.Id, (hwnd, _) => {
                var title = new StringBuilder(100);
                GetWindowText(hwnd, title, 100);
                if (title.ToString() != "AGL PTZ Controller") return true;
                posted = PostMessage(hwnd, 16, IntPtr.Zero, IntPtr.Zero);
                return false;
            }, IntPtr.Zero);
            if (posted) return true;
        }
        return false;
    }
}
'@
    }
    Get-ChildItem -LiteralPath $bin -Force | Where-Object Name -notin @('Data','portable.flag') |
        Copy-Item -Destination $test -Recurse
    New-Item -ItemType File -Path "$test/portable.flag"|Out-Null
    New-Item -ItemType Directory -Path "$test/Data"|Out-Null
    @{enabled=$true;adapterId=$AdapterId;port=$Port}|ConvertTo-Json -Compress |
        Set-Content -LiteralPath "$test/Data/remote-settings.json" -Encoding ASCII
    $savedAudio=$env:AGL_PTZ_TEST_AUDIO
    $saved=$env:AGL_PTZ_TEST
    try {
        $env:AGL_PTZ_TEST='1'
        if($AudioOnly){$env:AGL_PTZ_TEST_AUDIO='1'}
        $app=Start-Process -FilePath "$test/AGL.PTZ.Controller.exe" -WindowStyle Hidden -PassThru
    } finally {$env:AGL_PTZ_TEST=$saved; $env:AGL_PTZ_TEST_AUDIO=$savedAudio}
    $sequence=0
    $first=Connect-TestPeer
    $second=Connect-TestPeer
    if($AudioOnly){
        Send $first @{type='audioVolume';target='speaker';value=33}
        Send $second @{type='audioMute';target='mic';value=$true}
        $null=Read-Until $first {param($m) $m.type -eq 'state' -and $m.cameras.Count -eq 0 -and
            $m.audio.speaker.Volume -eq 33 -and $m.audio.mic.Muted}
        Send $second @{type='audioVolume';target='speaker';value=77}
        $null=Read-Until $first {param($m) $m.type -eq 'state' -and $m.audio.speaker.Volume -eq 77}
        Send $first @{type='audioMute';target='mic';value=$false}
        $null=Read-Until $second {param($m) $m.type -eq 'state' -and $m.audio.mic.Muted -eq $false}
        'PASS: two paired clients, audio without cameras, last write wins'
        return
    }
    Send $first @{type='bankActivate';cameraId='test-usb';requestId='test'}
    $state=Read-Until $first {param($m) $m.type -eq 'state' -and $m.canMove}
    'PASS: hello and redacted state'
    Move-Test $first $state
    if(!(Test-Path -LiteralPath "$test/Data/test-motion.log")){throw 'No motion observed.'}
    "PASS: silence stop $((Check-Stop ((Tick)-50) 500)) ms"
    Start-Sleep -Milliseconds 1100
    Move-Test $first $state 4
    Move-Test $second $state 1
    $null=Read-Until $second {param($m) $m.code -eq 'busy'}
    Send $second @{type='stop'}
    "PASS: busy and cross-client stop $((Check-Stop (Tick) 300)) ms"
    Send $first @{type='input';seq=(++$sequence);cameraId='test-usb';generation=-1;stick=@{x=1;y=0};zoomHold=0}
    $null=Read-Until $first {param($m) $m.code -eq 'bad_generation'}
    $garbage=[Text.Encoding]::UTF8.GetBytes('{')
    $null=$first.SendAsync([ArraySegment[byte]]::new($garbage),[Net.WebSockets.WebSocketMessageType]::Text,$true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Move-Test $first $state 80 -Flood
    Start-Sleep -Milliseconds 1050
    Move-Test $first $state 4
    $first.Abort()
    "PASS: stale generation, malformed input, flood and abort stop $((Check-Stop (Tick) 300)) ms"
    $previous=Get-Content -LiteralPath "$test/Data/test-motion.log" -Tail 1
    Move-Test $second $state 8
    if((Get-Content -LiteralPath "$test/Data/test-motion.log" -Tail 1) -eq $previous){throw 'Expected motion before shutdown.'}
    if(![PtzTestClock]::Close($app) -or !$app.WaitForExit(4000)){throw 'Controller did not close during motion.'}
    'PASS: controller closed during motion'
} finally {
    foreach($ws in $sockets){$ws.Dispose()}
    if($app -and !$app.HasExited){$app.Kill();$app.WaitForExit()}
    $item=Get-Item -LiteralPath $test
    if($item.FullName -eq $test -and $item.Name -like 'agl-socket-*' -and
        !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        # WebView2 helper processes outlive the killed app and keep the temporary profile locked:
        # stop only those whose command line points into this test folder.
        Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.Contains($test) } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        for($attempt=0;$attempt -lt 40;$attempt++){
            try { Remove-Item -LiteralPath $test -Recurse -Force -ErrorAction Stop; break }
            catch { if($attempt -eq 39){Write-Warning "Could not remove $test (profile still locked)"}; Start-Sleep -Milliseconds 500 }
        }
    }
}
