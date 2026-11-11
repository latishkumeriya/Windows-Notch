# CI visual check: runs NotchWin on the runner's desktop over a colourful backdrop and
# captures the notch (collapsed, expanded, clipboard) in dark and light mode.
param([string]$Exe = "publish\NotchWin.exe", [string]$Out = "screenshots")

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "Screen: $($screen.Width)x$($screen.Height)"

function Shot($name) {
  $w = [Math]::Min(900, $screen.Width); $h = 340
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int](($screen.Width - $w) / 2), 0, 0, 0, (New-Object System.Drawing.Size $w, $h))
  $bmp.Save((Join-Path $Out "$name.png"))
  $g.Dispose(); $bmp.Dispose()
}

function Hotkey([byte]$key) {
  [U]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero); [U]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
  [U]::keybd_event($key, 0, 0, [UIntPtr]::Zero); [U]::keybd_event($key, 0, 2, [UIntPtr]::Zero)
  [U]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [U]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
}

function Set-Theme([int]$light) {
  $k = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"
  New-Item -Force $k | Out-Null
  Set-ItemProperty $k AppsUseLightTheme $light -Type DWord
  Set-ItemProperty $k EnableTransparency 1 -Type DWord
}

# Colourful backdrop so blur/transparency is visible in the captures.
$backdrop = @'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$f = New-Object System.Windows.Forms.Form
$f.FormBorderStyle = "None"; $f.WindowState = "Maximized"; $f.BackColor = "White"
$colors = "#E53935","#FB8C00","#FDD835","#43A047","#1E88E5","#8E24AA","#00ACC1","#F06292"
foreach ($c in $colors) { $p = New-Object System.Windows.Forms.Panel; $p.Dock = "Left"; $p.Width = 130; $p.BackColor = [System.Drawing.ColorTranslator]::FromHtml($c); $f.Controls.Add($p) }
$l = New-Object System.Windows.Forms.Label; $l.Text = "GLASS TEST GLASS TEST GLASS TEST"; $l.Font = New-Object System.Drawing.Font("Segoe UI", 40, [System.Drawing.FontStyle]::Bold)
$l.AutoSize = $true; $l.Top = 20; $l.Left = 0; $l.BackColor = "Transparent"; $f.Controls.Add($l); $l.BringToFront()
[System.Windows.Forms.Application]::Run($f)
'@
$bdFile = Join-Path $env:TEMP "backdrop.ps1"; Set-Content $bdFile $backdrop
$bd = Start-Process powershell -ArgumentList "-NoProfile -File `"$bdFile`"" -PassThru
Start-Sleep 4

foreach ($mode in @(@{ Name = "dark"; Light = 0 }, @{ Name = "light"; Light = 1 })) {
  Set-Theme $mode.Light
  [U]::SetCursorPos([int]($screen.Width / 2), 500) | Out-Null
  $app = Start-Process $Exe -PassThru
  Start-Sleep 6
  "Some copied text from CI", "https://github.com/latish2/Hoplite", "Another clipboard entry" | ForEach-Object { Set-Clipboard $_; Start-Sleep -Milliseconds 600 }

  # Password-manager style secret: must NOT be recorded.
  $do = New-Object System.Windows.Forms.DataObject
  $do.SetData([System.Windows.Forms.DataFormats]::UnicodeText, "SECRET-PASSWORD-123")
  $do.SetData("CanIncludeInClipboardHistory", (New-Object IO.MemoryStream (, [BitConverter]::GetBytes([int]0))))
  [System.Windows.Forms.Clipboard]::SetDataObject($do, $true)
  Start-Sleep -Milliseconds 600

  $img = New-Object System.Drawing.Bitmap 320, 200
  $gi = [System.Drawing.Graphics]::FromImage($img); $gi.Clear([System.Drawing.Color]::MediumPurple); $gi.Dispose()
  [System.Windows.Forms.Clipboard]::SetImage($img)
  Start-Sleep 2
  Shot "$($mode.Name)-1-collapsed"

  [U]::SetCursorPos([int]($screen.Width / 2), 8) | Out-Null
  Start-Sleep 2
  Shot "$($mode.Name)-2-expanded-media"

  [U]::SetCursorPos([int]($screen.Width / 2), 600) | Out-Null
  Start-Sleep 2
  Hotkey 0x56   # Ctrl+Alt+V
  Start-Sleep 2
  Shot "$($mode.Name)-3-clipboard"

  Stop-Process -Id $app.Id -Force
  Start-Sleep 1
}

Stop-Process -Id $bd.Id -Force -ErrorAction SilentlyContinue

# ---- security checks on persisted clipboard history ----
$dir = Join-Path $env:APPDATA "NotchWin"
$dat = Join-Path $dir "clipboard.dat"
if (!(Test-Path $dat)) { throw "clipboard history was not saved" }
$raw = [IO.File]::ReadAllBytes($dat)
if ([Text.Encoding]::UTF8.GetString($raw).Contains("Some copied text")) { throw "clipboard history is stored in plaintext" }
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction SilentlyContinue
$json = [Text.Encoding]::UTF8.GetString([System.Security.Cryptography.ProtectedData]::Unprotect(
  $raw, [Text.Encoding]::UTF8.GetBytes("NotchWin.clipboard.v1"), "CurrentUser"))
if (!$json.Contains("Some copied text")) { throw "history missing expected entry" }
if ($json.Contains("SECRET-PASSWORD")) { throw "private (CanIncludeInClipboardHistory=0) content was recorded" }
if (!$json.Contains('"Kind":2')) { throw "image clip missing" }
if (Get-ChildItem (Join-Path $dir "clips") -Filter *.png) { throw "plaintext clip images on disk" }
if (!(Get-ChildItem (Join-Path $dir "clips") -Filter *.bin)) { throw "encrypted clip image missing" }
Write-Host "Security checks passed: history encrypted, private content skipped, images encrypted."
$log = Join-Path $env:APPDATA "NotchWin\error.log"
if (Test-Path $log) { Write-Host "---- error.log ----"; Get-Content $log }
