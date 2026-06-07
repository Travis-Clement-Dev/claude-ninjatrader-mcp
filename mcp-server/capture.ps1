# capture.ps1 — screenshot a window (by title substring) or the full virtual desktop.
# DirectX charts (NinjaTrader) capture correctly via screen-grab (not window-print),
# so we grab the screen REGION the window occupies. Requires the window to be visible.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File capture.ps1 -WindowTitle "Chart" -OutPath out.png
param(
    [string]$WindowTitle = "",
    [string]$OutPath = "$env:TEMP\ocm_capture.png"
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class OcmWin32 {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$script:foundRect = $null
if ($WindowTitle -ne "") {
    $cb = [OcmWin32+EnumProc] {
        param($h, $l)
        if (-not [OcmWin32]::IsWindowVisible($h)) { return $true }
        $len = [OcmWin32]::GetWindowTextLength($h)
        if ($len -le 0) { return $true }
        $sb = New-Object System.Text.StringBuilder ($len + 1)
        [void][OcmWin32]::GetWindowText($h, $sb, $sb.Capacity)
        if ($sb.ToString() -like "*$WindowTitle*") {
            $r = New-Object OcmWin32+RECT
            if ([OcmWin32]::GetWindowRect($h, [ref]$r) -and ($r.Right - $r.Left) -gt 0) {
                $script:foundRect = $r
                return $false   # stop enumerating
            }
        }
        return $true
    }
    [void][OcmWin32]::EnumWindows($cb, [IntPtr]::Zero)
}

if ($script:foundRect -ne $null) {
    $r = $script:foundRect
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
} else {
    $vs = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $x = $vs.X; $y = $vs.Y; $w = $vs.Width; $h = $vs.Height
}

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output $OutPath
