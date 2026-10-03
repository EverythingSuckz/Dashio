# Builds src\Dashio.App\Assets\AppIcon.ico from the two drawings beside it:
# AppIcon.svg, and AppIconSmall.svg, a plainer one for the sizes where the detail would blur.
# It also builds the installer's pictures in installer\art from side.svg, side-dark.svg and the logo.
# Microsoft Edge draws them, without opening a window. Run from anywhere:  pwsh tools\make-icon.ps1

Add-Type -AssemblyName System.Drawing

$assets = (Resolve-Path (Join-Path $PSScriptRoot '..\src\Dashio.App\Assets')).Path
$output = Join-Path $assets 'AppIcon.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$smallUpTo = 32
$drawnAt = 1024

$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge is needed to draw the icon and was not found.' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) "dashio-icon-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $work | Out-Null

function Get-Drawing([string]$svg, [int]$width = $drawnAt, [int]$height = $drawnAt) {
    $page = Join-Path $work 'page.html'
    $png = Join-Path $work 'drawing.png'
    $source = ([uri]([System.IO.Path]::IsPathRooted($svg) ? $svg : (Join-Path $assets $svg))).AbsoluteUri
    "<html><body style='margin:0;background:transparent'><img src='$source' width='$width' height='$height'></body></html>" |
        Set-Content $page -Encoding utf8
    $arguments = '--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000',
        "--user-data-dir=`"$(Join-Path $work 'profile')`"", "--window-size=$width,$height",
        "--screenshot=`"$png`"", ([uri]$page).AbsoluteUri
    $process = Start-Process $edge -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(60000) -or -not (Test-Path $png)) { throw "Edge did not draw $svg." }
    $bytes = [System.IO.File]::ReadAllBytes($png)
    Remove-Item $png
    return [System.Drawing.Bitmap]::new([System.IO.MemoryStream]::new($bytes))
}

function New-IconPng([System.Drawing.Bitmap]$drawing, [int]$size, [int]$height = 0) {
    if ($height -eq 0) { $height = $size }
    $bitmap = [System.Drawing.Bitmap]::new($size, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    # Without this the edge pixels are blended with a border that is not there.
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $g.DrawImage($drawing, [System.Drawing.Rectangle]::new(0, 0, $size, $height), 0, 0, $drawing.Width, $drawing.Height,
        [System.Drawing.GraphicsUnit]::Pixel, $attributes)
    $g.Dispose()
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    return , $stream.ToArray()
}

try {
    $full = Get-Drawing 'AppIcon.svg'
    $small = Get-Drawing 'AppIconSmall.svg'
    $images = foreach ($size in $sizes) { , (New-IconPng ($size -le $smallUpTo ? $small : $full) $size) }

    # An .ico file is a small directory followed by the images; PNG images are allowed at any size.
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
    Write-Host "Wrote $output ($($file.Length) bytes, sizes: $($sizes -join ', '))"

    # Every size side by side on a light and a dark strip, for a quick look.
    $preview = Join-Path ([System.IO.Path]::GetTempPath()) 'dashio-icon-preview.png'
    $sheet = [System.Drawing.Bitmap]::new(720, 560)
    $sg = [System.Drawing.Graphics]::FromImage($sheet)
    $sg.Clear([System.Drawing.Color]::FromArgb(243, 243, 243))
    $sg.FillRectangle([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(32, 32, 32)), 0, 280, 720, 280)
    foreach ($top in 12, 292) {
        $x = 12
        for ($i = $sizes.Count - 1; $i -ge 0; $i--) {
            $image = [System.Drawing.Image]::FromStream([System.IO.MemoryStream]::new($images[$i]))
            $sg.DrawImageUnscaled($image, $x, $top)
            $x += $sizes[$i] + 16
        }
    }
    $sg.Dispose()
    $sheet.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Preview: $preview"

    # The installer's pictures, one of each for 100, 150, 200 and 250% scaling.
    $art = (Resolve-Path (Join-Path $PSScriptRoot '..\installer\art')).Path
    $light = Get-Drawing (Join-Path $art 'side.svg') 820 1570
    $dark = Get-Drawing (Join-Path $art 'side-dark.svg') 820 1570
    foreach ($scale in 100, 150, 200, 250) {
        [System.IO.File]::WriteAllBytes((Join-Path $art "side-light-$scale.png"), (New-IconPng $light (164 * $scale / 100) (314 * $scale / 100)))
        [System.IO.File]::WriteAllBytes((Join-Path $art "side-dark-$scale.png"), (New-IconPng $dark (164 * $scale / 100) (314 * $scale / 100)))
        [System.IO.File]::WriteAllBytes((Join-Path $art "small-$scale.png"), (New-IconPng $full (58 * $scale / 100)))
    }
    Write-Host "Wrote the installer's pictures to $art"
}
finally {
    Start-Sleep -Milliseconds 500
    try { [System.IO.Directory]::Delete($work, $true) } catch { }
}

# The four-square mark shown for Windows itself in the lists.
$logoPath = Join-Path $assets 'WindowsLogo.png'
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
Write-Host "Wrote $logoPath"
