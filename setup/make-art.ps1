<#
.SYNOPSIS
  Draws the installer artwork: setup\dialog.bmp, setup\banner.bmp and setup\logo.png.

.DESCRIPTION
  WixUI ships a dark red bitmap with a disc on it, which is what an installer looked like in 2004. These
  replace it with the same AirPod glyph the tray and the app icon use, in the app's own dark grey.

  Sizes are fixed by WixUI: the dialog bitmap is 493x312, the banner 493x58. Only the left 164 px of the
  dialog bitmap is ours to paint - Windows Installer draws the dialog text in black straight onto the rest
  of it, so that part stays white or the "Completed" page becomes unreadable.

  Run it after changing the palette; the bitmaps are committed, so a normal build does not need it.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$dark = [System.Drawing.Color]::FromArgb(255, 28, 28, 30)
$white = [System.Drawing.Color]::FromArgb(255, 245, 245, 247)

# One AirPod: a bud and a stem rounded at both ends, in a 32x32 grid, same shape as TrayIcons.Draw.
function Add-Pod($graphics, $brush, $x, $y, $size) {
    $s = $size / 32.0
    $graphics.FillEllipse($brush, $x + 6 * $s, $y + 2.5 * $s, 17 * $s, 16 * $s)
    $stem = New-Object System.Drawing.Drawing2D.GraphicsPath
    $stem.AddArc($x + 17.5 * $s, $y + 9.5 * $s, 7 * $s, 7 * $s, 180, 180)
    $stem.AddArc($x + 17.5 * $s, $y + 22 * $s, 7 * $s, 7 * $s, 0, 180)
    $stem.CloseFigure()
    $graphics.FillPath($brush, $stem)
    $stem.Dispose()
}

function New-Canvas($width, $height) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::White)
    return @($bitmap, $g)
}

$here = $PSScriptRoot

# --- the dialog bitmap: dark column on the left, white where the installer writes ---------------------
$canvas = New-Canvas 493 312
$bitmap = $canvas[0]
$g = $canvas[1]
$column = New-Object System.Drawing.SolidBrush $dark
$g.FillRectangle($column, 0, 0, 164, 312)
$pod = New-Object System.Drawing.SolidBrush $white
Add-Pod $g $pod 42 84 80
$font = New-Object System.Drawing.Font 'Segoe UI Light', 21, ([System.Drawing.FontStyle]::Regular)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$g.DrawString('PodGate', $font, $pod, (New-Object System.Drawing.RectangleF 0, 188, 164, 40), $format)
$g.Dispose()
$bitmap.Save((Join-Path $here 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bitmap.Dispose()

# --- the banner: white, with the glyph on its disc at the right, where WixUI expects a logo -----------
$canvas = New-Canvas 493 58
$bitmap = $canvas[0]
$g = $canvas[1]
$g.FillEllipse($column, 434, 9, 40, 40)
Add-Pod $g $pod 434 9 40
$g.Dispose()
$bitmap.Save((Join-Path $here 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bitmap.Dispose()

# --- the bundle's logo: the setup.exe window shows this next to the licence ---------------------------
$logo = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::White)
$g.FillEllipse($column, 0, 0, 64, 64)
Add-Pod $g $pod 0 0 64
$g.Dispose()
$logo.Save((Join-Path $here 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

$column.Dispose(); $pod.Dispose(); $font.Dispose()
Write-Host "Wrote dialog.bmp, banner.bmp and logo.png"
