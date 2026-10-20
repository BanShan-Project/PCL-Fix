[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$WorkRoot = (Get-Location).Path,
    [int]$TimeoutSeconds = 60,
    [int]$ObserveSeconds = 10
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Fresh launcher folder and cache. Do not place the EXE beneath the child's TEMP:
# PCL intentionally rejects launches from the system extraction/temp directory.
$inputExe = (Resolve-Path -LiteralPath $Executable).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path ([IO.Path]::GetFullPath($WorkRoot)) ('pcl-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $output, $work | Out-Null
$cache = Join-Path $work 'isolated-temp'
New-Item -ItemType Directory -Path $cache | Out-Null
$localExe = Join-Path $work 'PCL.exe'
Copy-Item -LiteralPath $inputExe -Destination $localExe
if ($work -match '(?i)AppData\\Local\\Temp\\') {
    throw 'Choose a WorkRoot outside AppData\Local\Temp; PCL refuses that location.'
}

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PclSmokeWindow {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr target, uint flags);
}
'@

$result = [ordered]@{
    passed = $false
    scope = 'Startup window smoke only; no login, game launch, download, or UI workflow was requested.'
    input_sha256 = (Get-FileHash -LiteralPath $inputExe -Algorithm SHA256).Hash.ToLowerInvariant()
    work_directory = $work
    window_title = $null
    window_handle = $null
    screenshot = $null
    screenshot_error = $null
    process_exit_code = $null
    error = $null
}
$process = $null
try {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $localExe
    $info.WorkingDirectory = $work
    $info.UseShellExecute = $false
    $info.EnvironmentVariables['TEMP'] = $cache
    $info.EnvironmentVariables['TMP'] = $cache
    $info.EnvironmentVariables['APPDATA'] = (Join-Path $work 'isolated-appdata')
    $info.EnvironmentVariables['LOCALAPPDATA'] = (Join-Path $work 'isolated-localappdata')
    $process = [Diagnostics.Process]::Start($info)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $seenAt = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            $result.process_exit_code = $process.ExitCode
            throw "Launcher exited before startup observation finished: $($process.ExitCode)"
        }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.MainWindowTitle) {
            if ($null -eq $seenAt) { $seenAt = [DateTime]::UtcNow }
            if (([DateTime]::UtcNow - $seenAt).TotalSeconds -ge $ObserveSeconds) { break }
        }
    }
    $process.Refresh()
    if ($process.MainWindowHandle -eq [IntPtr]::Zero -or $null -eq $seenAt -or
        ([DateTime]::UtcNow - $seenAt).TotalSeconds -lt $ObserveSeconds) {
        throw 'No stable launcher window appeared before the timeout.'
    }
    $result.window_title = $process.MainWindowTitle
    $result.window_handle = $process.MainWindowHandle.ToInt64()
    # Do not count an OS error dialog as successful startup. A first-run prompt is
    # reported by its actual title and remains a startup-only observation.
    if ($result.window_title -match '(?i)error|exception|初始化错误|运行环境错误|需要解压|应用程序错误') {
        throw "Launcher reported an error window: $($result.window_title)"
    }
    try {
        $rect = [PclSmokeWindow+Rect]::new()
        if (-not [PclSmokeWindow]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed' }
        $bitmap = [Drawing.Bitmap]::new([Math]::Max(1, $rect.Right - $rect.Left), [Math]::Max(1, $rect.Bottom - $rect.Top))
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $dc = $graphics.GetHdc()
        try {
            if (-not [PclSmokeWindow]::PrintWindow($process.MainWindowHandle, $dc, 2)) { throw 'PrintWindow failed' }
        } finally { $graphics.ReleaseHdc($dc) }
        $screenshot = Join-Path $output 'startup-window.png'
        $bitmap.Save($screenshot, [Drawing.Imaging.ImageFormat]::Png)
        $graphics.Dispose()
        $bitmap.Dispose()
        $result.screenshot = $screenshot
    } catch { $result.screenshot_error = $_.Exception.Message }
    $result.passed = $true
} catch {
    $result.error = $_.Exception.Message
} finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        }
        $result.process_exit_code = $process.ExitCode
        $process.Dispose()
    }
    $logs = Join-Path $output 'logs'
    New-Item -ItemType Directory -Force -Path $logs | Out-Null
    Get-ChildItem -LiteralPath $work -Recurse -File | Where-Object {
        $_.Extension -in '.log', '.txt', '.ini', '.json'
    } | ForEach-Object {
        $relative = $_.FullName.Substring($work.Length).TrimStart('\', '/')
        $destination = Join-Path $logs $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination -ErrorAction Continue
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'startup-smoke.json') -Encoding utf8
}
$result | ConvertTo-Json -Depth 5
if (-not $result.passed) { exit 1 }
