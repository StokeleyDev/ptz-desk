param([Parameter(Mandatory)][string]$BinPath,[Parameter(Mandatory)][string]$AdapterId,
    [ValidateRange(1025,65535)][int]$PanelPort=8787,[ValidateRange(1025,65535)][int]$HubPort=8788)
# Hub test: a "panel" instance (audio only) and a "hub" instance that pairs with it via
# AGL_PTZ_TEST_HUB_LINK. Phones talk to the hub; the panel's own clients see the changes.
$ErrorActionPreference='Stop'
if(Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue){throw 'Close AGL PTZ Controller before running the test.'}
$bin=(Resolve-Path -LiteralPath $BinPath).Path
if(($bin -split '[\\/]') -notcontains 'bin'){throw 'Use bin output.'}
$roots=@{}
$apps=@{}
$sockets=@()
Add-Type -AssemblyName System.Net.Http
Add-Type -AssemblyName System.Security
if(!('PtzHubClose' -as [type])){
    Add-Type @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class PtzHubClose {
    private delegate bool WindowCallback(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(int id, WindowCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l);
    public static bool Close(Process process) {
        foreach (ProcessThread thread in process.Threads) {
            bool posted = false;
            EnumThreadWindows(thread.Id, (hwnd, _) => {
                var title = new StringBuilder(100);
                GetWindowText(hwnd, title, 100);
                if (title.ToString() != "PTZ Desk") return true;
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
function Send($ws,$message){
    $bytes=[Text.Encoding]::UTF8.GetBytes(($message|ConvertTo-Json -Compress -Depth 5))
    $null=$ws.SendAsync([ArraySegment[byte]]::new($bytes),[Net.WebSockets.WebSocketMessageType]::Text,$true,
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
$script:last=@{}
# State-based wait: the newest state already received counts, then wait for later ones.
function Await-State($ws,[scriptblock]$match,[int]$TimeoutMs=5000){
    $known=$script:last[$ws.GetHashCode()]
    if($known -and (& $match $known)){return $known}
    return Read-Until $ws $match $TimeoutMs
}
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
                $json=[Text.Encoding]::UTF8.GetString($stream.ToArray())
                $data=ConvertFrom-Json $json
                if($data.type -eq 'state' -and $json -match '"(username|hasPassword|report|address|secret|origin)"|agl-test-usb|(?:\d{1,3}\.){3}\d{1,3}'){throw "Private state leaked: $json"}
                if($data.type -eq 'state'){$script:last[$ws.GetHashCode()]=$data}
                if(& $match $data){return $data}
            } finally {$stream.Dispose()}
        } while($true)
    } finally {$timeout.Dispose()}
}
function New-Instance([string]$Name,[int]$Port,[hashtable]$Env){
    $root=(New-Item -ItemType Directory -Path (Join-Path $env:TEMP ("agl-hub-$Name-"+[guid]::NewGuid()))).FullName
    $roots[$Name]=$root
    Get-ChildItem -LiteralPath $bin -Force | Where-Object Name -notin @('Data','portable.flag') | Copy-Item -Destination $root -Recurse
    New-Item -ItemType File -Path "$root/portable.flag"|Out-Null
    New-Item -ItemType Directory -Path "$root/Data"|Out-Null
    @{enabled=$true;adapterId=$AdapterId;port=$Port}|ConvertTo-Json -Compress |
        Set-Content -LiteralPath "$root/Data/remote-settings.json" -Encoding ASCII
}
function Start-Instance([string]$Name,[hashtable]$Env){
    $saved=@{}
    foreach($key in $Env.Keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key);[Environment]::SetEnvironmentVariable($key,$Env[$key])}
    try { $apps[$Name]=Start-Process -FilePath "$($roots[$Name])/AGL.PTZ.Controller.exe" -WindowStyle Hidden -PassThru }
    finally { foreach($key in $Env.Keys){[Environment]::SetEnvironmentVariable($key,$saved[$key])} }
}
function Wait-Offer([string]$Name,[string]$Previous=''){
    $path=Join-Path $roots[$Name] 'Data/test-pairing.txt'
    for($i=0;$i -lt 120;$i++){
        if(Test-Path -LiteralPath $path){
            try{$url=[IO.File]::ReadAllText($path)}catch{$url=''}
            if($url -and $url -ne $Previous){return $url}
        }
        Start-Sleep -Milliseconds 125
    }
    throw "No pairing offer for $Name."
}
function Connect-Peer([string]$Url){
    $uri=[uri]$Url
    $origin=$uri.GetLeftPart([UriPartial]::Authority)
    $code=$uri.Fragment.Substring(6)
    $handler=[Net.Http.HttpClientHandler]::new()
    $handler.UseProxy=$false
    $client=[Net.Http.HttpClient]::new($handler)
    try {
        $client.DefaultRequestHeaders.Add('Origin',$origin)
        $body=[Net.Http.StringContent]::new((@{code=$code;name='Hub test'}|ConvertTo-Json -Compress),[Text.Encoding]::UTF8,'application/json')
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
try {
    New-Instance 'panel' $PanelPort @{}
    New-Instance 'hub' $HubPort @{}
    $common=@{AGL_PTZ_TEST='1';AGL_PTZ_TEST_AUDIO='1'}
    Start-Instance 'panel' ($common+@{AGL_PTZ_INSTANCE='panel'})
    $panelOffer=Wait-Offer 'panel'
    $hubDevices=Start-Instance 'hub' ($common+@{AGL_PTZ_INSTANCE='hub';AGL_PTZ_TEST_HUB_LINK=$panelOffer})
    $hubOffer=Wait-Offer 'hub'
    $phone=Connect-Peer $hubOffer
    $room=(Await-State $phone {param($m) $m.type -eq 'state' -and @($m.rooms).Count -eq 1 -and $m.rooms[0].online -and $m.rooms[0].audio} 15000).rooms[0]
    'PASS: hub paired with the panel by link, room online, no addresses in phone state'
    $rooms=Get-Content -LiteralPath "$($roots['hub'])/Data/rooms.json" -Raw | ConvertFrom-Json
    if(@($rooms).Count -ne 1 -or $rooms[0].secret.Length -lt 40 -or (Get-Content "$($roots['hub'])/Data/rooms.json" -Raw) -match 'agl_ptz_device'){throw 'rooms.json is not protected.'}
    $plain=[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($rooms[0].secret),$null,'CurrentUser'))
    if($plain.Length -lt 20){throw 'DPAPI secret did not round-trip.'}
    'PASS: room key stored with DPAPI'

    # The panel's own phone watches what the hub changes.
    $panelNext=$null
    $panelOfferNext=Wait-Offer 'panel' $panelOffer
    $local=Connect-Peer $panelOfferNext
    Send $phone @{type='roomAudioVolume';roomId=$room.id;target='speaker';value=21}
    Send $phone @{type='roomAudioMute';roomId=$room.id;target='mic';value=$true}
    $null=Await-State $local {param($m) $m.type -eq 'state' -and $m.audio.speaker.Volume -eq 21 -and $m.audio.mic.Muted}
    $null=Await-State $phone {param($m) $m.type -eq 'state' -and $m.rooms[0].audio.speaker.Volume -eq 21 -and $m.rooms[0].audio.mic.Muted}
    'PASS: phone -> hub -> panel volume and mute'
    Send $phone @{type='roomAudioVolume';roomId='nope';target='speaker';value=5}
    Send $phone @{type='roomAudioVolume';roomId=$room.id;target='speaker';value=500}
    Send $local @{type='audioVolume';target='speaker';value=66}
    $state=Await-State $phone {param($m) $m.type -eq 'state' -and $m.rooms[0].audio.speaker.Volume -eq 66}
    'PASS: panel change reaches the phone, invalid room and value ignored'

    if(![PtzHubClose]::Close($apps['panel']) -or !$apps['panel'].WaitForExit(6000)){throw 'Panel did not close.'}
    $null=Await-State $phone {param($m) $m.type -eq 'state' -and !$m.rooms[0].online} 10000
    'PASS: panel gone -> room offline'
    Start-Instance 'panel' ($common+@{AGL_PTZ_INSTANCE='panel'})
    $null=Await-State $phone {param($m) $m.type -eq 'state' -and $m.rooms[0].online -and $m.rooms[0].audio} 40000
    'PASS: panel back -> hub reconnected without re-pairing'
} finally {
    foreach($ws in $sockets){$ws.Dispose()}
    foreach($app in $apps.Values){ if($app -and !$app.HasExited){$app.Kill();$app.WaitForExit()} }
    foreach($root in $roots.Values){
        $item=Get-Item -LiteralPath $root -ErrorAction SilentlyContinue
        if($item -and $item.Name -like 'agl-hub-*' -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){
            Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" -ErrorAction SilentlyContinue |
                Where-Object { $_.CommandLine -and $_.CommandLine.Contains($root) } |
                ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
            for($attempt=0;$attempt -lt 40;$attempt++){
                try { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction Stop; break }
                catch { if($attempt -eq 39){Write-Warning "Could not remove $root"}; Start-Sleep -Milliseconds 500 }
            }
        }
    }
}
