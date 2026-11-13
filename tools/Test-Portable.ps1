param([Parameter(Mandatory = $true)][string]$ReleasePath,
    # Newer WebView2 runtimes fail to start on a secondary desktop. By default the test runs on the
    # interactive desktop (a window is visible for about a minute); use -IsolatedDesktop to keep the old behaviour.
    [switch]$IsolatedDesktop)
$ErrorActionPreference='Stop'
if (Get-Process -Name 'AGL.PTZ.Controller' -ErrorAction SilentlyContinue) {
    throw 'Close AGL PTZ Controller before running the test (the single-instance mutex is shared).'
}
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class PtzDesktopTest
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct Startup {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, xsize, ysize, xchars, ychars, fill, flags;
        public short show, reserved2;
        public IntPtr data, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Proc { public IntPtr process, thread; public int pid, tid; }
    [DllImport("user32", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, int access, IntPtr attrs);
    [DllImport("user32")] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32")] private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32")] private static extern bool EnumChildWindows(IntPtr hwnd, EnumWindowsProc callback, IntPtr param);
    [DllImport("user32")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] public static extern bool ShowWindowAsync(IntPtr hwnd, int mode);
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd, uint msg, IntPtr wparam, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)]
    private static extern bool CreateProcess(string app, StringBuilder args, IntPtr p, IntPtr t, bool inherit,
        int flags, IntPtr env, string dir, ref Startup startup, out Proc proc);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
    public static Process Start(string exe, string args, string desktop)
    {
        var startup = new Startup {cb=Marshal.SizeOf(typeof(Startup)),desktop=string.IsNullOrEmpty(desktop)?null:desktop};
        Proc proc;
        if (!CreateProcess(exe, new StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero, IntPtr.Zero,
            false, 0, IntPtr.Zero, System.IO.Path.GetDirectoryName(exe), ref startup, out proc))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(proc.process); CloseHandle(proc.thread);
        return Process.GetProcessById(proc.pid);
    }
    public static IntPtr Window(IntPtr desktop, int id)
    {
        IntPtr match=IntPtr.Zero;
        EnumDesktopWindows(desktop, (h, _) => {
            int pid; GetWindowThreadProcessId(h,out pid);
            var title=new StringBuilder(200); GetWindowText(h,title,200);
            if(pid==id && IsWindowVisible(h) && title.Length>0) {match=h; return false;}
            return true;
        }, IntPtr.Zero);
        return match;
    }
    public static string[] Children(IntPtr window)
    {
        var all=new List<string>();
        EnumChildWindows(window, (h, _) => {
            var text=new StringBuilder(512);
            var cls=new StringBuilder(256);
            GetClassName(h,cls,256);
            IntPtr unused;
            if (IsWindowVisible(h)) {
                SendMessageTimeout(h,13,(IntPtr)512,text,2,50,out unused);
                all.Add(cls+" "+text);
            }
            return true;
        },IntPtr.Zero);
        return all.ToArray();
    }
}
'@
$release=(Resolve-Path -LiteralPath $ReleasePath).Path
$contents=@(Get-ChildItem -LiteralPath "$release/Portable" -Force)
if ($contents.Count -ne 2 -or ($contents | Where-Object {
    $_.PSIsContainer -or $_.Name -notin @('AGL.PTZ.Controller.exe', 'portable.flag')
})) { throw 'The release Portable directory must contain only the EXE and portable.flag' }
$test=Join-Path ([IO.Path]::GetTempPath()) ('ptz-portable-test-' + [guid]::NewGuid().ToString('N'))
Copy-Item -LiteralPath "$release/Portable" -Destination $test -Recurse
$exe=Join-Path $test 'AGL.PTZ.Controller.exe'
$local=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'AGL PTZ Controller'
function AppDataSnapshot {
    if (Test-Path -LiteralPath $local) {
        Get-ChildItem -LiteralPath $local -Recurse -Force | ForEach-Object { "$($_.FullName):$($_.LastWriteTimeUtc.Ticks)" }
    }
}
$before=@(AppDataSnapshot)
if ($IsolatedDesktop) {
    $name='AglPtzTest' + [guid]::NewGuid().ToString('N')
    $desktop=[PtzDesktopTest]::CreateDesktop($name,[IntPtr]::Zero,[IntPtr]::Zero,0,0x1ff,[IntPtr]::Zero)
    if ($desktop -eq [IntPtr]::Zero) { throw ('CreateDesktop error ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
} else {
    # EnumDesktopWindows(NULL) enumerates the calling thread's (interactive) desktop.
    $name=$null
    $desktop=[IntPtr]::Zero
}
function Wait-Until($condition) {
    for($i=0;$i -lt 75;$i++){ if (& $condition) { return $true }; Start-Sleep -Milliseconds 200 }
    return $false
}
function Close-Test($process,$window) {
    if ($null -ne $process -and !$process.HasExited) {
        if ($window -ne [IntPtr]::Zero) { [PtzDesktopTest]::PostMessage($window,16,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null }
        if (!$process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
    }
}
$first=$null;$second=$null;$fallback=$null
$firstWindow=[IntPtr]::Zero;$fallbackWindow=[IntPtr]::Zero
$savedEnvironment = @{}
foreach ($key in 'DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_MULTILEVEL_LOOKUP','PATH',
    'DOTNET_BUNDLE_EXTRACT_BASE_DIR','WEBVIEW2_BROWSER_EXECUTABLE_FOLDER') {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:DOTNET_ROOT=(New-Item -ItemType Directory -Path "$test/empty").FullName
    $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP='0'
    $env:PATH="$env:SystemRoot\System32;$env:SystemRoot"
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $test 'extract'
    Remove-Item Env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER -ErrorAction SilentlyContinue
    $first=[PtzDesktopTest]::Start($exe,'--demo',$name)
    if (!(Wait-Until { [PtzDesktopTest]::Window($desktop,$first.Id) -ne [IntPtr]::Zero })) {
        if ($first.HasExited) { throw "Demo exited: $($first.ExitCode)" }
        throw 'Demo window not found'
    }
    $firstWindow=[PtzDesktopTest]::Window($desktop,$first.Id)
    if (!(Wait-Until { ([PtzDesktopTest]::Children($firstWindow) -join ' ') -match 'Chrome_WidgetWin' })) {
        throw ('Embedded WebView window not visible: ' + ([PtzDesktopTest]::Children($firstWindow) -join ' / '))
    }
    if (!(Test-Path -LiteralPath "$test/Data/WebView2")) { throw 'Missing portable WebView2 data' }
    'PASS: self-contained demo, visible embedded WebView2, portable Data'
    [PtzDesktopTest]::ShowWindowAsync($firstWindow,6) | Out-Null
    if (!(Wait-Until { [PtzDesktopTest]::IsIconic($firstWindow) })) { throw 'Could not minimize demo window' }
    $second=[PtzDesktopTest]::Start($exe,'--demo',$name)
    if (!$second.WaitForExit(10000)) { throw "Second instance still running; first exited: $($first.HasExited)" }
    if (!(Wait-Until { ![PtzDesktopTest]::IsIconic($firstWindow) })) { throw 'First instance did not restore' }
    'PASS: second instance exited and restored first window'
    Close-Test $first $firstWindow
    $env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER="$test/empty"
    $fallback=[PtzDesktopTest]::Start($exe,'--demo',$name)
    if (!(Wait-Until { [PtzDesktopTest]::Window($desktop,$fallback.Id) -ne [IntPtr]::Zero })) { throw 'Fallback window missing' }
    $fallbackWindow=[PtzDesktopTest]::Window($desktop,$fallback.Id)
    if (!(Wait-Until { ([PtzDesktopTest]::Children($fallbackWindow) -join ' ') -match 'AGL\.PTZ\.Dependencies\.cmd' })) {
        throw ('Fallback banner missing: ' + ([PtzDesktopTest]::Children($fallbackWindow) -join ' / '))
    }
    'PASS: fallback banner with missing WebView2 emulation'
    Close-Test $fallback $fallbackWindow
    Remove-Item Env:WEBVIEW2_BROWSER_EXECUTABLE_FOLDER -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath "$release/Portable/Data") { throw 'The test modified release Portable' }
    $after=@(AppDataSnapshot)
    if (($before -join "`n") -ne ($after -join "`n")) { throw 'LocalAppData changed' }
    'PASS: LocalAppData unchanged'
} finally {
    Close-Test $first $firstWindow
    Close-Test $second ([IntPtr]::Zero)
    Close-Test $fallback $fallbackWindow
    if ($desktop -ne [IntPtr]::Zero) { [PtzDesktopTest]::CloseDesktop($desktop) | Out-Null }
    # WebView2 helper processes may keep the temporary profile locked for a moment; clean up quietly.
    for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath $test); $i++) {
        try { Remove-Item -LiteralPath $test -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 500 }
    }
    foreach ($key in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key])
    }
}
