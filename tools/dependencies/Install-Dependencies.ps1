$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$bootstrapper = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703'
$clientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'

function Get-WebView2Version {
    $keys = @(
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$clientId",
        "HKCU:\Software\Microsoft\EdgeUpdate\Clients\$clientId"
    )
    foreach ($key in $keys) {
        $value = (Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue).pv
        if (![string]::IsNullOrWhiteSpace($value) -and $value.Trim() -ne '0.0.0.0') {
            return $value.Trim()
        }
    }
}

$download = $null
try {
    $version = Get-WebView2Version
    if (!$version) {
        Write-Host 'WebView2 Runtime — не найден.'
        $redist = Join-Path $PSScriptRoot 'redist'
        $installer = @('MicrosoftEdgeWebView2RuntimeInstallerX64.exe', 'MicrosoftEdgeWebview2Setup.exe') |
            ForEach-Object { Join-Path $redist $_ } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if (!$installer) {
            Write-Host 'Загрузка установщика Microsoft…'
            $download = Join-Path ([IO.Path]::GetTempPath()) ("AGL-PTZ-WebView2-" + [guid]::NewGuid() + ".exe")
            Invoke-WebRequest -Uri $bootstrapper -UseBasicParsing -TimeoutSec 120 -OutFile $download
            $installer = $download
        }
        Write-Host 'Установка WebView2 Runtime…'
        $process = Start-Process -FilePath $installer -ArgumentList '/silent', '/install' -Wait -PassThru -WindowStyle Hidden
        $version = Get-WebView2Version
        if (!$version) { throw "WebView2 не найден после установки. Код установщика: $($process.ExitCode)." }
    }
    Write-Host "WebView2 Runtime — установлен ($version)."
    Write-Host 'Все зависимости на месте.'
    exit 0
}
catch {
    Write-Host "Не удалось установить WebView2: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($download) { Remove-Item -LiteralPath $download -Force -ErrorAction SilentlyContinue }
}
