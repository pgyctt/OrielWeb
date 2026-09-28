# Generates samples/OrielDemo/app.ico: a rounded-square mark in the demo palette with a white
# "webview window" glyph. Each size is drawn natively (not downscaled) so 16/24/32 stay legible,
# and the .ico stores every size as a PNG payload (supported since Windows Vista).
# ASCII-only.

param(
    [string]$OutFile = (Join-Path $PSScriptRoot 'app.ico'),
    [string]$PreviewPng = "$env:TEMP\oriel-icon-preview.png"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # background: rounded square with a diagonal gradient in the demo accent palette
    $pad = $s * 0.02
    $rect = New-Object System.Drawing.RectangleF($pad, $pad, ($s - 2 * $pad), ($s - 2 * $pad))
    $radius = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc(($rect.Right - $d), $rect.Y, $d, $d, 270, 90)
    $path.AddArc(($rect.Right - $d), ($rect.Bottom - $d), $d, $d, 0, 90)
    $path.AddArc($rect.X, ($rect.Bottom - $d), $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 0x5B, 0x9B, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0x27, 0x5F, 0xD0),
        45.0)
    $g.FillPath($brush, $path)

    # glyph: window outline + title-bar divider, both white
    $stroke = [Math]::Max(1.0, $s * 0.055)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $stroke)
    $pen.Alignment = [System.Drawing.Drawing2D.PenAlignment]::Inset
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $wx = $s * 0.27
    $wy = $s * 0.29
    $ww = $s - 2 * $wx
    $wh = $s * 0.42
    $wRadius = $s * 0.07
    $wd = $wRadius * 2
    $win = New-Object System.Drawing.Drawing2D.GraphicsPath
    $win.AddArc($wx, $wy, $wd, $wd, 180, 90)
    $win.AddArc(($wx + $ww - $wd), $wy, $wd, $wd, 270, 90)
    $win.AddArc(($wx + $ww - $wd), ($wy + $wh - $wd), $wd, $wd, 0, 90)
    $win.AddArc($wx, ($wy + $wh - $wd), $wd, $wd, 90, 90)
    $win.CloseFigure()
    $g.DrawPath($pen, $win)

    $lineY = $wy + [Math]::Max($wh * 0.3, $stroke * 1.6)
    $g.DrawLine($pen, [single]($wx + $stroke / 2), [single]$lineY, [single]($wx + $ww - $stroke / 2), [single]$lineY)

    $pen.Dispose(); $win.Dispose(); $brush.Dispose(); $path.Dispose()
    $g.Dispose()
    return $bmp
}

# --- render each size to PNG bytes ---
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @{ Size = $s; Bytes = $ms.ToArray() }
    if ($s -eq 256) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $ms.Dispose(); $bmp.Dispose()
}

# --- assemble the .ico container (PNG payloads, Vista+) ---
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$pngs.Count)

$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$dim)            # width  (0 means 256)
    $bw.Write([byte]$dim)            # height
    $bw.Write([byte]0)               # palette count
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # color planes
    $bw.Write([uint16]32)            # bits per pixel
    $bw.Write([uint32]$p.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $p.Bytes.Length
}
foreach ($p in $pngs) { $bw.Write($p.Bytes) }

$bw.Dispose(); $fs.Dispose()

Write-Output ("wrote " + $OutFile)
Get-Item $OutFile | Select-Object @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } | Format-Table -AutoSize | Out-String -Width 40
Write-Output ("sizes: " + (($pngs | ForEach-Object { $_.Size }) -join ', '))
Write-Output ("preview png: " + $PreviewPng)
