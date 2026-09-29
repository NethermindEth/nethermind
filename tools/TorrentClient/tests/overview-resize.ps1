# Requires the MAUI app to show a selected torrent's Overview tab.
param(
    [int]$ProcessId = 0,
    [int[]]$Widths = @(875, 870)
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TorrentResizeTestWindow
{
    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    public struct Rect { public int Left, Top, Right, Bottom; }
}
'@

if ($ProcessId -eq 0) {
    $ProcessId = (Get-Process Nethermind.Torrent.Maui | Select-Object -First 1).Id
}

$handle = (Get-Process -Id $ProcessId).MainWindowHandle
$original = New-Object TorrentResizeTestWindow+Rect
if (-not [TorrentResizeTestWindow]::GetWindowRect($handle, [ref]$original)) {
    throw 'Could not read the app window bounds.'
}

try {
    foreach ($width in $Widths) {
        if (-not [TorrentResizeTestWindow]::MoveWindow($handle, $original.Left, $original.Top, $width, 760, $true)) {
            throw "Could not resize the app to $width px."
        }

        Start-Sleep -Milliseconds 400
        $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $elements = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $bounds = @{}
        foreach ($element in $elements) {
            $name = $element.Current.Name
            if ($name -in @('Integrity', 'Connections', 'Trackers')) {
                $rect = $element.Current.BoundingRectangle
                if ($rect.Width -gt 0 -and $rect.Height -gt 0) {
                    $bounds[$name] = $rect
                }
            }
        }

        if (-not $bounds.ContainsKey('Integrity') -or -not $bounds.ContainsKey('Connections')) {
            throw "At $width px, show a selected torrent's Overview tab before running this check."
        }
        if ([Math]::Abs($bounds.Integrity.Top - $bounds.Connections.Top) -gt 2) {
            throw "At $width px, Connections moved below Integrity."
        }
        if (-not $bounds.ContainsKey('Trackers') -or
            $bounds.Trackers.Height -lt 16 -or
            $bounds.Trackers.Right -gt $original.Left + $width - 20) {
            throw "At $width px, the Connections rows are clipped."
        }

        Write-Output "$width px: Connections and Trackers are fully visible."
    }
}
finally {
    [TorrentResizeTestWindow]::MoveWindow(
        $handle, $original.Left, $original.Top,
        $original.Right - $original.Left, $original.Bottom - $original.Top, $true) | Out-Null
}
