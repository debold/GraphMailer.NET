<#
.SYNOPSIS
    Generates the GraphMailer application icons.

.DESCRIPTION
    Draws a shared base icon (white envelope on a blue Fluent-style tile, matching
    the ConfigTool accent color #0078D4) and adds a per-app badge:
      - Service     → green play-symbol badge   → src\GraphMailer.Service\Assets\graphmailer.ico
      - ConfigTool  → slate gear badge          → src\GraphMailer.ConfigTool\Assets\graphmailer.ico

    Output is a multi-resolution .ico with PNG-compressed entries
    (256, 128, 64, 48, 32, 24, 16 px). Re-run after changing the artwork.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent

# ── Drawing helpers ───────────────────────────────────────────────────────────

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$radius) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($x,          $y,          $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $path.AddArc($x,          $y + $h - $d, $d, $d,  90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-BaseTile([System.Drawing.Graphics]$g) {
    # Rounded tile with a vertical gradient around the app accent color #0078D4
    $tile = New-RoundedRectPath 8 8 240 240 52
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 8)),
        (New-Object System.Drawing.Point(0, 248)),
        [System.Drawing.Color]::FromArgb(255, 0x2B, 0x9B, 0xE8),
        [System.Drawing.Color]::FromArgb(255, 0x00, 0x5E, 0xA6))
    $g.FillPath($grad, $tile)
    $grad.Dispose(); $tile.Dispose()

    # Envelope body
    $envelope = New-RoundedRectPath 44 78 168 108 14
    $g.FillPath([System.Drawing.Brushes]::White, $envelope)
    $envelope.Dispose()

    # Envelope flap (V shape) in tile blue
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x0F, 0x6C, 0xBD), 13)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLine($pen,  52,  88, 128, 146)
    $g.DrawLine($pen, 204,  88, 128, 146)
    $pen.Dispose()
}

function Draw-Badge([System.Drawing.Graphics]$g, [System.Drawing.Color]$fill, [scriptblock]$symbol) {
    $cx = 184; $cy = 184; $rOuter = 64; $rInner = 54

    # White ring so the badge separates cleanly from the tile
    $white = [System.Drawing.Brushes]::White
    $g.FillEllipse($white, $cx - $rOuter, $cy - $rOuter, $rOuter * 2, $rOuter * 2)

    $brush = New-Object System.Drawing.SolidBrush($fill)
    $g.FillEllipse($brush, $cx - $rInner, $cy - $rInner, $rInner * 2, $rInner * 2)
    $brush.Dispose()

    & $symbol $g $cx $cy
}

$playSymbol = {
    param($g, $cx, $cy)
    $points = @(
        (New-Object System.Drawing.PointF(($cx - 17), ($cy - 26))),
        (New-Object System.Drawing.PointF(($cx - 17), ($cy + 26))),
        (New-Object System.Drawing.PointF(($cx + 29), $cy))
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon([System.Drawing.PointF[]]$points)
    $g.FillPath([System.Drawing.Brushes]::White, $path)
    $path.Dispose()
}

# The gear IS the badge silhouette: a filled circle behind a small gear reads
# like a stop/prohibition sign at taskbar size, so the teeth must define the
# outline. White ring underneath separates it from the tile.
function Draw-GearBadge([System.Drawing.Graphics]$g) {
    $cx = 184; $cy = 184

    $g.FillEllipse([System.Drawing.Brushes]::White, ($cx - 62), ($cy - 62), 124, 124)

    $slate = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x4A, 0x54, 0x5E))
    $state = $g.Save()
    $g.TranslateTransform($cx, $cy)

    # 8 prominent teeth (radius 28 → 52), then the gear body and a white hub hole
    for ($i = 0; $i -lt 8; $i++) {
        $g.RotateTransform(45)
        $g.FillRectangle($slate, -11, -52, 22, 26)
    }
    $g.FillEllipse($slate, -38, -38, 76, 76)
    $g.FillEllipse([System.Drawing.Brushes]::White, -16, -16, 32, 32)
    $slate.Dispose()
    $g.Restore($state)
}

# ── ICO writer (PNG-compressed entries) ───────────────────────────────────────

function Save-Ico([System.Drawing.Bitmap]$master, [string]$path) {
    $sizes = 256, 128, 64, 48, 32, 24, 16
    $pngs = foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gg = [System.Drawing.Graphics]::FromImage($bmp)
        $gg.InterpolationMode = 'HighQualityBicubic'
        $gg.SmoothingMode = 'AntiAlias'
        $gg.PixelOffsetMode = 'HighQuality'
        $gg.DrawImage($master, (New-Object System.Drawing.Rectangle(0, 0, $s, $s)))
        $gg.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , $ms.ToArray()
    }

    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $fs = [System.IO.File]::Create($path)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([uint16]0)               # reserved
        $bw.Write([uint16]1)               # type: icon
        $bw.Write([uint16]$sizes.Count)

        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dim = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }   # 0 = 256 in ICO
            $bw.Write([byte]$dim); $bw.Write([byte]$dim)
            $bw.Write([byte]0); $bw.Write([byte]0)
            $bw.Write([uint16]1); $bw.Write([uint16]32)
            $bw.Write([uint32]$pngs[$i].Length)
            $bw.Write([uint32]$offset)
            $offset += $pngs[$i].Length
        }
        foreach ($png in $pngs) { $bw.Write($png) }
    }
    finally { $bw.Dispose() }

    Write-Host "Written: $path"
}

# ── Render both variants ──────────────────────────────────────────────────────

function New-IconBitmap([scriptblock]$drawBadge) {
    $bmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    Draw-BaseTile $g
    & $drawBadge $g
    $g.Dispose()
    return $bmp
}

$serviceBmp = New-IconBitmap { param($g)
    Draw-Badge $g ([System.Drawing.Color]::FromArgb(255, 0x10, 0x7C, 0x10)) $playSymbol }
Save-Ico $serviceBmp (Join-Path $root 'src\GraphMailer.Service\Assets\graphmailer.ico')
$serviceBmp.Dispose()

$configBmp = New-IconBitmap { param($g) Draw-GearBadge $g }
Save-Ico $configBmp (Join-Path $root 'src\GraphMailer.ConfigTool\Assets\graphmailer.ico')
$configBmp.Dispose()

# Badge-less base icon as PNG — used by the ConfigTool sidebar logo
$baseBmp = New-Object System.Drawing.Bitmap(256, 256, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($baseBmp)
$g.SmoothingMode = 'AntiAlias'
$g.PixelOffsetMode = 'HighQuality'
Draw-BaseTile $g
$g.Dispose()
$basePath = Join-Path $root 'src\GraphMailer.ConfigTool\Assets\graphmailer-base.png'
$baseBmp.Save($basePath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Written: $basePath"

# Badge-less base icon as .ico — neutral product mark for the installer (setup.exe + Add/Remove Programs)
Save-Ico $baseBmp (Join-Path $root 'installer\graphmailer.ico')
$baseBmp.Dispose()

Write-Host 'Done.'
