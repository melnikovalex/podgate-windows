<#
.SYNOPSIS
  Builds src\PodGate.App\PodGate.ico from the designed app icon in assets\icons\app.

.DESCRIPTION
  One .ico drives four things: the executable, the setup.exe (Bundle IconSourceFile), the Installed-apps
  entry (ARPPRODUCTICON) and the Start-menu shortcut. Regenerate it here and all four follow.

  Every size is written as a 32-bit DIB rather than PNG. PNG-compressed icon entries are legal and smaller,
  but System.Drawing cannot decode them - it throws on any entry above 64 px - so an .ico built that way is
  unreadable to half the tooling that touches it. At this size the extra few hundred KB buy nothing.

  Each size has its own drawing, so they are taken as they are and never scaled: the small ones are hinted
  by hand and downscaling a big one would undo that. A missing size is an error rather than a silent resize.

  The white mark is the one that goes in: Windows shows a single icon whatever the theme, and the taskbar,
  Start menu and Installed-apps list are dark by default. On a light background it has little contrast -
  the artwork carries no plate of its own - which is a deliberate trade, not an oversight.

  Run after changing the artwork; PodGate.ico is committed, so a normal build does not need it.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$art = Join-Path $PSScriptRoot 'icons\app'
$target = Join-Path $root 'src\PodGate.App\PodGate.ico'

$sizes = 16, 20, 24, 32, 48, 64, 128, 256

# Copied pixel for pixel into a bitmap this code owns, so LockBits below reads a known 32bpp layout.
function Get-Artwork($size) {
    $file = Join-Path $art "app-dark-$size.png"
    if (-not (Test-Path $file)) { throw "no artwork for $size px: $file" }
    $original = [System.Drawing.Image]::FromFile($file)
    if ($original.Width -ne $size -or $original.Height -ne $size) {
        $original.Dispose()
        throw "app-dark-$size.png is not $size x $size"
    }
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImageUnscaled($original, 0, 0)
    $g.Dispose()
    $original.Dispose()
    return $bitmap
}

# A 32-bit DIB icon image: the header, the pixels bottom-up, then an all-zero AND mask. The mask is
# ignored for 32-bit images but the format still demands it, correctly sized and 4-byte aligned per row.
function Get-DibBytes($bitmap) {
    $size = $bitmap.Width
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $stream

    $writer.Write([int]40)              # BITMAPINFOHEADER
    $writer.Write([int]$size)
    $writer.Write([int]($size * 2))     # XOR and AND stacked
    $writer.Write([int16]1)
    $writer.Write([int16]32)
    $writer.Write([int]0)               # BI_RGB
    $maskStride = [Math]::Floor(($size + 31) / 32) * 4
    $writer.Write([int]($size * $size * 4 + $maskStride * $size))
    $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0); $writer.Write([int]0)

    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                             [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] ($size * 4)
    for ($y = $size - 1; $y -ge 0; $y--) {
        [System.Runtime.InteropServices.Marshal]::Copy(
            [IntPtr]($data.Scan0.ToInt64() + $y * $data.Stride), $row, 0, $row.Length)
        $writer.Write($row)
    }
    $bitmap.UnlockBits($data)

    $writer.Write((New-Object byte[] ($maskStride * $size)))
    $writer.Flush()
    return $stream.ToArray()
}

$images = @()
foreach ($size in $sizes) {
    $artwork = Get-Artwork $size
    $images += , @{ Size = $size; Bytes = (Get-DibBytes $artwork) }
    $artwork.Dispose()
}

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([int16]0)                 # reserved
$writer.Write([int16]1)                 # type: icon
$writer.Write([int16]$images.Count)

# 6-byte header plus one 16-byte directory entry per image, then the images themselves.
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }   # 256 is stored as 0
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)              # no palette
    $writer.Write([byte]0)
    $writer.Write([int16]1)             # planes
    $writer.Write([int16]32)            # bits per pixel
    $writer.Write([int]$image.Bytes.Length)
    $writer.Write([int]$offset)
    $offset += $image.Bytes.Length
}
# The cast is load-bearing: a hashtable property is typed object, and without it PowerShell picks
# BinaryWriter.Write(bool) and writes one byte per image instead of the image.
foreach ($image in $images) { $writer.Write([byte[]]$image.Bytes) }
$writer.Flush()

[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose()
Write-Host "Wrote $target - $($images.Count) sizes, $([Math]::Round((Get-Item $target).Length / 1KB)) KB"
