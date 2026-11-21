# Generates the Monitor Switcher icon: a monitor with two opposing arrows.
# Outputs assets/icon/icon-<size>.png (also used by the macOS app) and the Windows app.ico.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$pngDir = Join-Path $root "assets/icon"
$icoPath = Join-Path $root "windows/src/MonitorSwitcher.App/Assets/app.ico"
New-Item -ItemType Directory -Force -Path $pngDir, (Split-Path $icoPath) | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 32.0
    $g.ScaleTransform($k, $k)

    # Stand
    $standBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x6B, 0x7A, 0x90))
    $g.FillRectangle($standBrush, 13.5, 22.5, 5.0, 4.5)
    $g.FillPath($standBrush, (New-RoundedRect 8.5 26.0 15.0 3.0 1.5))

    # Screen
    $screen = New-RoundedRect 1.5 3.5 29.0 20.0 3.5
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 3.5), (New-Object System.Drawing.PointF 0, 23.5), ([System.Drawing.Color]::FromArgb(255, 0x4F, 0x8D, 0xF7)), ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x56, 0xD8)))
    $g.FillPath($grad, $screen)

    # Two opposing arrows (thicker at small sizes so they stay legible)
    $lw = if ($size -le 20) { 2.8 } elseif ($size -le 32) { 2.4 } else { 2.1 }
    $white = [System.Drawing.Color]::White
    $pen = New-Object System.Drawing.Pen $white, $lw
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $headBrush = New-Object System.Drawing.SolidBrush $white
    $hl = 4.6; $hh = 3.0
    # top: pointing right
    $g.DrawLine($pen, 7.5, 10.0, (24.5 - $hl + 0.5), 10.0)
    $g.FillPolygon($headBrush, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 24.5, 10.0), (New-Object System.Drawing.PointF (24.5 - $hl), (10.0 - $hh)), (New-Object System.Drawing.PointF (24.5 - $hl), (10.0 + $hh))))
    # bottom: pointing left
    $g.DrawLine($pen, 24.5, 17.0, (7.5 + $hl - 0.5), 17.0)
    $g.FillPolygon($headBrush, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 7.5, 17.0), (New-Object System.Drawing.PointF (7.5 + $hl), (17.0 - $hh)), (New-Object System.Drawing.PointF (7.5 + $hl), (17.0 + $hh))))

    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

# Classic DIB frame (BITMAPINFOHEADER + BGRA bottom-up + empty AND mask) for small sizes.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $w; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $rowBytes = [int]([Math]::Ceiling($w / 32.0) * 4)
    $bw.Write((New-Object byte[] ($rowBytes * $h)))
    $bw.Flush()
    return $ms.ToArray()
}

foreach ($s in 16, 32, 64, 128, 256, 512, 1024) {
    $b = New-IconBitmap $s
    $b.Save((Join-Path $pngDir "icon-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $b.Dispose()
}

$icoSizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $icoSizes) {
    $b = New-IconBitmap $s
    $bytes = if ($s -ge 256) { Get-PngBytes $b } else { Get-DibBytes $b }
    $b.Dispose()
    , @($s, $bytes)
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $len = $f[1].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$len); $w.Write([int]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Output "Wrote $icoPath and PNGs in $pngDir"
