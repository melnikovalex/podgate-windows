<#
.SYNOPSIS
  Draws the installer artwork: setup\dialog.bmp, setup\banner.bmp and setup\logo.png.

.DESCRIPTION
  WixUI ships a dark red bitmap with a disc on it, which is what an installer looked like in 2004. These
  replace it with the app's own icon, composited from assets\icons.

  Sizes are fixed by WixUI: the dialog bitmap is 493x312, the banner 493x58. Only the left 164 px of the
  dialog bitmap is ours to paint - Windows Installer draws the dialog text in black straight onto the rest
  of it, so that part stays white or the "Completed" page becomes unreadable. The banner keeps its left
  side clear for the same reason, so the icon sits on the right.

  BMP carries no transparency, so everything is composited onto its background here rather than relying on
  the alpha channel.

  Run it after changing the artwork; the bitmaps are committed, so a normal build does not need them.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = $PSScriptRoot
$assets = Join-Path (Split-Path $here -Parent) 'assets\icons'
$dark = [System.Drawing.Color]::FromArgb(255, 28, 28, 30)
$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

function New-Canvas($width, $height, $background) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear($background)
    return @($bitmap, $g)
}

function Add-Art($graphics, $file, $x, $y, $size) {
    $image = [System.Drawing.Image]::FromFile((Join-Path $assets $file))
    $graphics.DrawImage($image, (New-Object System.Drawing.Rectangle $x, $y, $size, $size))
    $image.Dispose()
}

# --- dialog.bmp: a dark column on the left, the rest left white for the installer's own text ------
$bitmap, $g = New-Canvas 493 312 $white
$column = New-Object System.Drawing.SolidBrush $dark
$g.FillRectangle($column, 0, 0, 164, 312)
$column.Dispose()
Add-Art $g 'mark\mark-white-512.png' 36 110 92
$g.Dispose()
$bitmap.Save((Join-Path $here 'dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bitmap.Dispose()

# --- banner.bmp: white, icon on the right; WixUI writes its heading over the left ------------------
$bitmap, $g = New-Canvas 493 58 $white
Add-Art $g 'app\app-dark-512.png' 431 7 44
$g.Dispose()
$bitmap.Save((Join-Path $here 'banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
$bitmap.Dispose()

# --- logo.png: the bootstrapper's logo in the setup.exe window, 64x64, transparency allowed --------
$logo = New-Object System.Drawing.Bitmap 64, 64
$g = [System.Drawing.Graphics]::FromImage($logo)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)
Add-Art $g 'app\app-dark-512.png' 0 0 64
$g.Dispose()
$logo.Save((Join-Path $here 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()

Write-Host "Wrote dialog.bmp, banner.bmp and logo.png"
