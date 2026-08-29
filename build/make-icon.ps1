<#
.SYNOPSIS
    Draws the application mark and writes the .ico and the PNGs the interface uses.

.DESCRIPTION
    The mark is an arrow coming down onto a shelf, in gold on a warm near-black tile. It says the
    one thing the program does. The music this application is largely used for belongs to the
    surfaces with room for it, not to a glyph that has to survive at sixteen pixels.

    Every size is drawn at its own scale rather than resampled from one large bitmap. A 256-pixel
    drawing shrunk to 16 turns into porridge: the shelf merges with the arrowhead and the gap
    between them closes. Below 32 pixels the strokes are thickened, because at that size only the
    silhouette survives.

    Also writes the glyph without its tile, for the surfaces that paint their own ground.

.NOTES
    Run after changing the palette:  powershell -File build\make-icon.ps1
    Velopack reads the resulting ICO directly for the installer and shortcuts.
#>
[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $PngDirectory
)

$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $here '..\src\MiguelDownloader.App\Assets' }
if (-not $PngDirectory) { $PngDirectory = Join-Path $here 'brand' }

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force -Path $OutputDirectory, $PngDirectory | Out-Null

# --- palette -----------------------------------------------------------------------------------
# Warm near-black so the gold sits on something that shares its temperature; a neutral grey ground
# makes the same gold look dirty.
$groundInner = [Drawing.Color]::FromArgb(255, 0x2A, 0x20, 0x0C)
$groundOuter = [Drawing.Color]::FromArgb(255, 0x0B, 0x09, 0x06)
$goldLight   = [Drawing.Color]::FromArgb(255, 0xFF, 0xDE, 0x7A)
$goldDeep    = [Drawing.Color]::FromArgb(255, 0xE0, 0x95, 0x0A)

function New-Mark {
    param([int] $Size, [switch] $GlyphOnly)

    $bmp = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality

    $detailed = $Size -ge 32
    $u = $Size / 1024.0            # everything below is authored on a 1024 grid
    function P([double] $v) { [single]($v * $u) }

    # --- ground: rounded tile with a warm glow rising from where the arrow lands ---
    # 18% of the width. The first attempt used 23%, which left so little straight edge that the
    # tile stopped reading as a square and turned into a dome.
    $r = P 184
    $tile = New-Object Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $tile.AddArc(0, 0, $d, $d, 180, 90)
    $tile.AddArc($Size - $d, 0, $d, $d, 270, 90)
    $tile.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
    $tile.AddArc(0, $Size - $d, $d, $d, 90, 90)
    $tile.CloseFigure()

    # Base coat first. A PathGradientBrush only paints inside its own path, so using one to fill
    # the tile left everything outside its ellipse unpainted: the corners vanished and the square
    # rendered as a dome. The flat gradient covers the shape; the glow is layered on top, clipped.
    $baseRect = New-Object Drawing.RectangleF(0, 0, [single]$Size, [single]$Size)
    $base = New-Object Drawing.Drawing2D.LinearGradientBrush(
        $baseRect, ([Drawing.Color]::FromArgb(255, 0x1C, 0x15, 0x09)), $groundOuter, 90.0)

    $glow = New-Object Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse((P 20), (P 300), (P 984), (P 900))
    $ground = New-Object Drawing.Drawing2D.PathGradientBrush $glow
    $ground.CenterColor = $groundInner
    $ground.SurroundColors = @([Drawing.Color]::FromArgb(0, 0x2A, 0x20, 0x0C))
    $ground.CenterPoint = New-Object Drawing.PointF((P 512), (P 800))

    if (-not $GlyphOnly) {
        $g.FillPath($base, $tile)
        $g.SetClip($tile)
        $g.FillPath($ground, $glow)
        $g.ResetClip()
    }
    $ground.Dispose(); $glow.Dispose()

    # --- the mark: an arrow coming down onto a shelf ---
    # One idea, not two. An earlier version hung a notehead off the shaft to say "music" as well,
    # and at any size below 64 it read as a lollipop rather than a note. The music belongs to the
    # gold and to the surfaces that have room for it; the icon says what the program does.
    # The rectangle spans the shaft down to the bottom of the shelf. Stopping it short made the
    # gradient wrap and lit the shelf by accident. Deep at the top and bright at the foot, so the
    # eye is pulled the way the arrow points.
    $markRect = New-Object Drawing.RectangleF((P 300), (P 244), (P 424), (P 664))
    $gold = New-Object Drawing.Drawing2D.LinearGradientBrush(
        $markRect, $goldDeep, $goldLight, 90.0)

    # Shaft.
    $stemW = if ($detailed) { 108 } else { 128 }
    $g.FillRectangle($gold, (P (512 - $stemW / 2)), (P 250), (P $stemW), (P 300))

    # Arrowhead, the widest part of the glyph and the piece that has to survive at 16 pixels.
    $head = New-Object Drawing.Drawing2D.GraphicsPath
    $half = if ($detailed) { 196 } else { 214 }
    $head.AddPolygon(@(
        (New-Object Drawing.PointF((P (512 - $half)), (P 520))),
        (New-Object Drawing.PointF((P (512 + $half)), (P 520))),
        (New-Object Drawing.PointF((P 512), (P 742)))
    ))
    $g.FillPath($gold, $head)
    $head.Dispose()

    # Shelf: what the download lands on. The gap above it is what stops the two shapes merging
    # into one blob when the icon is 16 pixels tall.
    $shelfH = if ($detailed) { 76 } else { 96 }
    $shelfW = if ($detailed) { 424 } else { 448 }
    $shelf = New-Object Drawing.Drawing2D.GraphicsPath
    $sx0 = 512 - $shelfW / 2
    $sy0 = 826
    $shelf.AddArc((P $sx0), (P $sy0), (P $shelfH), (P $shelfH), 90, 180)
    $shelf.AddArc((P ($sx0 + $shelfW - $shelfH)), (P $sy0), (P $shelfH), (P $shelfH), 270, 180)
    $shelf.CloseFigure()
    $g.FillPath($gold, $shelf)
    $shelf.Dispose()

    $base.Dispose(); $gold.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

# --- write the .ico ----------------------------------------------------------------------------
# System.Drawing cannot save a multi-size icon, so the container is written by hand.
#
# Entries up to 64 pixels are stored as BMP and the two large ones as PNG, which is the layout
# Windows itself ships. A file made entirely of PNG entries is smaller and renders correctly in
# Explorer and WPF, but older readers -- including System.Drawing's own Icon class, which is what
# a build script or an installer is most likely to use -- cannot decode it and fail outright.
function ConvertTo-IcoBitmap {
    param([Drawing.Bitmap] $Bitmap)

    $size = $Bitmap.Width
    $stride = $size * 4
    $maskStride = [int](([Math]::Floor(($size + 31) / 32)) * 4)

    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms

    # BITMAPINFOHEADER. The height is doubled because the AND mask counts as part of the image.
    $w.Write([UInt32]40)
    $w.Write([Int32]$size)
    $w.Write([Int32]($size * 2))
    $w.Write([UInt16]1)
    $w.Write([UInt16]32)
    $w.Write([UInt32]0)
    $w.Write([UInt32]($stride * $size + $maskStride * $size))
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)

    $data = $Bitmap.LockBits(
        (New-Object Drawing.Rectangle 0, 0, $size, $size),
        [Drawing.Imaging.ImageLockMode]::ReadOnly,
        [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $row = New-Object byte[] $stride
    try {
        # Bottom-up, which is how a BMP stores its rows.
        for ($y = $size - 1; $y -ge 0; $y--) {
            [Runtime.InteropServices.Marshal]::Copy(
                [IntPtr]($data.Scan0.ToInt64() + ($y * $data.Stride)), $row, 0, $stride)
            $w.Write($row)
        }
    }
    finally { $Bitmap.UnlockBits($data) }

    # The AND mask is left empty: every pixel carries its own alpha in the 32-bit image above.
    $blank = New-Object byte[] ($maskStride * $size)
    $w.Write($blank)

    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose(); $ms.Dispose()
    return $bytes
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$payloads = @()
foreach ($s in $sizes) {
    $bmp = New-Mark -Size $s

    if ($s -ge 128) {
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    }
    else {
        $bytes = ConvertTo-IcoBitmap -Bitmap $bmp
    }
    $payloads += , @{ Size = $s; Bytes = $bytes }

    if ($s -in @(48, 256)) {
        $bmp.Save((Join-Path $PngDirectory "mark-$s.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
}

# The glyph on its own, without the tile. The installer panel and the application's welcome screen
# paint their own ground, and a tile pasted onto them reads as a sticker rather than as a mark.
$glyph = New-Mark -Size 512 -GlyphOnly
$glyph.Save((Join-Path $PngDirectory 'glyph-512.png'), [Drawing.Imaging.ImageFormat]::Png)
$glyph.Dispose()

$icoPath = Join-Path $OutputDirectory 'migueldownloader.ico'
$fs = [IO.File]::Create($icoPath)
$w = New-Object IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$payloads.Count)

$offset = 6 + (16 * $payloads.Count)
foreach ($p in $payloads) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$p.Bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $payloads) {
    $bytes = [byte[]] $p.Bytes
    $w.Write($bytes, 0, $bytes.Length)
}
$w.Flush(); $w.Close(); $fs.Dispose()

# The directory records an offset and a length for every entry. If the file is shorter than the
# last of them, a reader follows an offset past the end and the icon fails to load somewhere far
# from here, so it is checked at the point where it can still be fixed.
$total = 0
foreach ($p in $payloads) { $total += $p.Bytes.Length }
$expected = 6 + (16 * $payloads.Count) + $total
$actual = (Get-Item $icoPath).Length
if ($actual -ne $expected) {
    throw "The icon is $actual bytes but its directory describes $expected."
}

'{0,-30} {1,7:N0} bytes  ({2} tamanhos)' -f 'migueldownloader.ico', (Get-Item $icoPath).Length, $payloads.Count
Get-ChildItem $PngDirectory -Filter 'mark-*.png' | ForEach-Object {
    '{0,-30} {1,7:N0} bytes' -f $_.Name, $_.Length
}
