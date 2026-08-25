# TUITester.ps1 — Automated TUI testing with visual feedback
# Usage: .\TUITester.ps1 --pid <PID> --title "window title" --sequence <actions>

param(
    [int]$TargetPid,
    [string]$Title,
    [string[]]$Sequence
)

$ErrorActionPreference = "Stop"
$InjectorPath = Join-Path $PSScriptRoot "ConsolePuppetInjector\bin\Release\net10.0\ConsolePuppetInjector.exe"
$ScreenshotDir = Join-Path $PSScriptRoot "tui-screenshots"
if (!(Test-Path $ScreenshotDir)) { New-Item -ItemType Directory -Force -Path $ScreenshotDir | Out-Null }

# Find the terminal window
$terminalHwnd = 0
if ($Title) {
    foreach ($proc in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($proc.MainWindowTitle -match [regex]::Escape($Title)) {
                $terminalHwnd = $proc.MainWindowHandle
                Write-Host "Found window: '$($proc.MainWindowTitle)' (hWnd=$($terminalHwnd.ToString('X')))" -ForegroundColor Green
                break
            }
        } finally { $proc.Dispose() }
    }
} elseif ($TargetPid) {
    # Try to get console window for PID
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] public static extern bool FreeConsole();
}
"@
    [Win32]::AttachConsole([uint32]$TargetPid)
    $terminalHwnd = [Win32]::GetConsoleWindow()
    [Win32]::FreeConsole()
    if ($terminalHwnd -ne [IntPtr]::Zero) {
        Write-Host "Got console window for PID $TargetPid (hWnd=$($terminalHwnd.ToString('X')))" -ForegroundColor Green
    } else {
        Write-Host "Warning: No console window found for PID $TargetPid" -ForegroundColor Yellow
    }
}

if ($terminalHwnd -eq [IntPtr]::Zero) {
    Write-Host "ERROR: Cannot find target window" -ForegroundColor Red
    exit 1
}

# Bring window to foreground
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
[Win32]::ShowWindow($terminalHwnd, 9)  # SW_RESTORE
Start-Sleep -Milliseconds 50
[Win32]::SetForegroundWindow($terminalHwnd)
Start-Sleep -Milliseconds 100

Write-Host ""
Write-Host "=== TUI Test Runner ===" -ForegroundColor Cyan
Write-Host "Target: hWnd=$($terminalHwnd.ToString('X'))" -ForegroundColor Cyan
Write-Host "Screenshots saved to: $ScreenshotDir" -ForegroundColor Cyan
Write-Host ""

$step = 0
foreach ($action in $Sequence) {
    $step++
    $parts = $action.Split(" ", [StringSplitOptions]::RemoveEmptyEntries)
    
    if ($parts[0] -eq "key") {
        $keyName = $parts[1]
        Write-Host "[$step] Injecting key: $keyName ..." -NoNewline -ForegroundColor White
        
        $result = & $InjectorPath --pid $TargetPid --key $keyName 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host " OK" -ForegroundColor Green
        } else {
            Write-Host " FAILED: $result" -ForegroundColor Red
        }
        
        Start-Sleep -Milliseconds 500
        
        # Capture screenshot using Windows API
        $screenshotFile = Join-Path $ScreenshotDir "step-$($step.ToString('D4')).png"
        Add-Type @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
public class ScreenCapture {
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    public static void Capture(IntPtr hwnd, string file) {
        try {
            var rect = new Native.RECT();
            Native.GetWindowRect(hwnd, ref rect);
            int w = rect.right - rect.left;
            int h = rect.bottom - rect.top;
            using var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.left, rect.top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        } catch (Exception ex) {
            File.WriteAllText(file.Replace(".png", ".error.txt"), ex.Message);
        }
    }
}
public class Native {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, ref RECT lpRect);
}
"@ -ErrorAction SilentlyContinue
        
        try {
            [ScreenCapture]::Capture($terminalHwnd, $screenshotFile)
            Write-Host "       Screenshot: $screenshotFile" -ForegroundColor DarkGray
        } catch {
            Write-Host "       (screenshot failed: $_)" -ForegroundColor DarkGray
        }
        
    } elseif ($parts[0] -eq "text") {
        $text = $parts[1]
        Write-Host "[$step] Typing text: '$text' ..." -NoNewline -ForegroundColor White
        
        $result = & $InjectorPath --pid $TargetPid --text $text 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host " OK" -ForegroundColor Green
        } else {
            Write-Host " FAILED: $result" -ForegroundColor Red
        }
        
        Start-Sleep -Milliseconds 500
        
        $screenshotFile = Join-Path $ScreenshotDir "step-$($step.ToString('D4')).png"
        try {
            [ScreenCapture]::Capture($terminalHwnd, $screenshotFile)
            Write-Host "       Screenshot: $screenshotFile" -ForegroundColor DarkGray
        } catch {
            Write-Host "       (screenshot failed: $_)" -ForegroundColor DarkGray
        }
        
    } elseif ($parts[0] -eq "wait") {
        $ms = if ($parts.Count -gt 1) { [int]$parts[1] } else { 1000 }
        Write-Host "[$step] Waiting ${ms}ms ..." -ForegroundColor DarkGray
        Start-Sleep -Milliseconds $ms
        
    } elseif ($parts[0] -eq "capture") {
        Write-Host "[$step] Capturing screenshot ..." -ForegroundColor DarkGray
        $screenshotFile = Join-Path $ScreenshotDir "step-$($step.ToString('D4')).png"
        try {
            [ScreenCapture]::Capture($terminalHwnd, $screenshotFile)
            Write-Host "       Saved: $screenshotFile" -ForegroundColor DarkGray
        } catch {
            Write-Host "       (failed: $_)" -ForegroundColor DarkGray
        }
    }
    
    Write-Host ""
}

Write-Host "=== Test complete: $step steps ===" -ForegroundColor Cyan
Write-Host "Screenshots directory: $ScreenshotDir" -ForegroundColor Cyan
Write-Host ""
Write-Host "To view screenshots:" -ForegroundColor Yellow
Write-Host "  explorer `$env:USERPROFILE\OneDrive\Sorgenti\AgentBridge\tui-screenshots" -ForegroundColor Gray
