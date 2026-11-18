$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$size = 64
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 59, 91, 219))
$card = New-RoundedPath 2 2 60 60 16
$g.FillPath($bg, $card)

$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$bubble = New-RoundedPath 13 15 38 27 10
$g.FillPath($white, $bubble)

$tail = New-Object System.Drawing.Drawing2D.GraphicsPath
$points = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF(21, 40)),
    (New-Object System.Drawing.PointF(20, 50)),
    (New-Object System.Drawing.PointF(31, 41))
)
$tail.AddPolygon($points)
$g.FillPath($white, $tail)

$dot = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 59, 91, 219))
$g.FillEllipse($dot, 20, 26, 5, 5)
$g.FillEllipse($dot, 29, 26, 5, 5)
$g.FillEllipse($dot, 38, 26, 5, 5)

$icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
$stream = [System.IO.File]::Create((Join-Path $assets 'app.ico'))
$icon.Save($stream)
$stream.Close()
$g.Dispose()
$bmp.Dispose()
Write-Output "wrote $assets\app.ico"
