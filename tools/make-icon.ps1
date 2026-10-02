# Draws the Dashio app icon and writes src\Dashio.App\Assets\AppIcon.ico.
# The icon is three switches on a rounded tile: two on, one off.
# Run from anywhere:  pwsh tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$output = Join-Path $PSScriptRoot '..\src\Dashio.App\Assets\AppIcon.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function New-RoundedRectangle([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconPng([int]$size) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # Everything below is drawn on a 256 x 256 canvas.
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $indigo = [System.Drawing.Color]::FromArgb(255, 84, 80, 214)
    $teal = [System.Drawing.Color]::FromArgb(255, 22, 176, 196)
    $knob = [System.Drawing.Color]::FromArgb(255, 60, 62, 184)
    $white = [System.Drawing.Color]::White

    $tile = New-RoundedRectangle 12 12 232 232 54
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(12, 12), [System.Drawing.PointF]::new(244, 244), $indigo, $teal)
    $g.FillPath($gradient, $tile)

    $trackX = 60; $trackW = 136; $trackH = 38; $knobSize = 26; $inset = 6
    $rows = @(
        @{ Y = 56; On = $true },
        @{ Y = 109; On = $true },
        @{ Y = 162; On = $false }
    )
    foreach ($row in $rows) {
        $track = New-RoundedRectangle $trackX $row.Y $trackW $trackH ($trackH / 2)
        if ($row.On) {
            $g.FillPath([System.Drawing.SolidBrush]::new($white), $track)
            $knobX = $trackX + $trackW - $inset - $knobSize
            $g.FillEllipse([System.Drawing.SolidBrush]::new($knob), $knobX, $row.Y + $inset, $knobSize, $knobSize)
        }
        else {
            $outline = New-RoundedRectangle ($trackX + 3) ($row.Y + 3) ($trackW - 6) ($trackH - 6) (($trackH - 6) / 2)
            $pen = [System.Drawing.Pen]::new($white, 6)
            $g.DrawPath($pen, $outline)
            $g.FillEllipse([System.Drawing.SolidBrush]::new($white), $trackX + $inset + 2, $row.Y + $inset + 2, $knobSize - 4, $knobSize - 4)
        }
    }

    $g.Dispose()
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

# An .ico file is a small directory followed by the images; PNG images are allowed at any size.
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

$file = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)              # reserved
$writer.Write([uint16]1)              # type: icon
$writer.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension)   # width (0 means 256)
    $writer.Write([byte]$dimension)   # height
    $writer.Write([byte]0)            # palette size
    $writer.Write([byte]0)            # reserved
    $writer.Write([uint16]1)          # colour planes
    $writer.Write([uint16]32)         # bits per pixel
    $writer.Write([uint32]$images[$i].Length)
    $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()

[System.IO.File]::WriteAllBytes($output, $file.ToArray())
Write-Host "Wrote $((Resolve-Path $output).Path) ($($file.Length) bytes, sizes: $($sizes -join ', '))"

# A PNG preview next to the script's temp output, for a quick look.
$preview = Join-Path ([System.IO.Path]::GetTempPath()) 'dashio-icon-preview.png'
[System.IO.File]::WriteAllBytes($preview, (New-IconPng 256))
Write-Host "Preview: $preview"

# The four-square mark shown for Windows itself in the lists.
$logoPath = Join-Path $PSScriptRoot '..\src\Dashio.App\Assets\WindowsLogo.png'
$logoSize = 128
$logo = [System.Drawing.Bitmap]::new($logoSize, $logoSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$lg = [System.Drawing.Graphics]::FromImage($logo)
$lg.Clear([System.Drawing.Color]::Transparent)
$blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
$margin = 14; $gap = 6
$square = ($logoSize - 2 * $margin - $gap) / 2
foreach ($x in $margin, ($margin + $square + $gap)) {
    foreach ($y in $margin, ($margin + $square + $gap)) { $lg.FillRectangle($blue, [single]$x, [single]$y, [single]$square, [single]$square) }
}
$lg.Dispose()
$logo.Save($logoPath, [System.Drawing.Imaging.ImageFormat]::Png)
$logo.Dispose()
Write-Host "Wrote $((Resolve-Path $logoPath).Path)"
